using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace GPTUnity
{
    /// <summary>
    /// Routes HTTP requests to main-thread Unity operations. Every handler runs its
    /// body on the main thread via MainThread.Execute and returns an "ok":true envelope,
    /// or throws ApiException for clean 4xx/5xx responses.
    /// </summary>
    public static class ApiRouter
    {
        static Dictionary<string, object> Ok() => new Dictionary<string, object> { { "ok", true } };

        static Dictionary<string, object> Fail(string error, string hint = null)
            => GPTUnityHttpServer.MakeError(error, hint);

        public static void Handle(string method, string path, string query, string body, HttpListenerResponse res)
        {
            switch ((method + " " + path).Trim())
            {
                case "GET /health":
                    MainThread.Execute(() =>
                    {
                        var s = SceneManager.GetActiveScene();
                        resRespond(res, Ok(new Dictionary<string, object>
                        {
                            { "service", "gptunity-bridge" },
                            { "version", Bridge.Version },
                            { "unityVersion", Application.unityVersion },
                            { "platform", Application.platform.ToString() },
                            { "projectName", ProjectName() },
                            { "isPlaying", EditorApplication.isPlaying },
                            { "isPaused", EditorApplication.isPaused },
                            { "activeScene", s.IsValid() ? s.name : null },
                            { "port", Bridge.Port }
                        }));
                    });
                    break;

                case "GET /status":
                    MainThread.Execute(() => resRespond(res, Ok(new Dictionary<string, object>
                    {
                        { "running", Bridge.ServerRunning },
                        { "port", Bridge.Port },
                        { "url", Bridge.BaseUrl },
                        { "connections", 0 }
                    })));
                    break;

                case "GET /project":
                    MainThread.Execute(() =>
                    {
                        var scenePaths = new List<string>();
                        foreach (var s in EditorBuildSettings.scenes)
                            scenePaths.Add(s.path);

                        resRespond(res, Ok(new Dictionary<string, object>
                        {
                            { "projectName", ProjectName() },
                            { "assetsPath", Application.dataPath },
                            { "unityVersion", Application.unityVersion },
                            { "buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                            { "activeScene", SceneManager.GetActiveScene().path },
                            { "buildSettingsScenes", scenePaths }
                        }));
                    });
                    break;

                case "GET /scenes":
                    MainThread.Execute(() =>
                    {
                        var guids = AssetDatabase.FindAssets("t:Scene");
                        var scenes = guids
                            .Select(AssetDatabase.GUIDToAssetPath)
                            .OrderBy(p => p)
                            .ToList();

                        var current = SceneManager.GetActiveScene();

                        var sceneInfos = scenes.Select(p => new Dictionary<string, object>
                        {
                            { "path", p },
                            { "name", Path.GetFileNameWithoutExtension(p) },
                            { "isActive", current.IsValid() && current.path == p }
                        }).ToList();

                        resRespond(res, Ok(new Dictionary<string, object>
                        {
                            { "count", sceneInfos.Count },
                            { "scenes", sceneInfos }
                        }));
                    });
                    break;

                case "POST /scene/open":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            if (EditorApplication.isPlaying)
                            {
                                resRespond(res, Fail("Cannot open a scene while the game is playing. Stop play mode first."), 400);
                                return;
                            }

                            string p = Json.GetString(bodyDict, "path");
                            if (string.IsNullOrEmpty(p))
                            {
                                resRespond(res, Fail("Missing \"path\" (scene asset path, e.g. Assets/Scenes/Main.unity)."), 400);
                                return;
                            }

                            if (!p.StartsWith("Assets/", StringComparison.Ordinal))
                            {
                                p = "Assets/" + p;
                            }
                            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(p) == null)
                            {
                                resRespond(res, Fail("Scene not found: " + p, "Use /scenes to list available scenes."), 404);
                                return;
                            }

                            var opened = EditorSceneManager.OpenScene(p, OpenSceneMode.Single);
                            resRespond(res, Ok(new Dictionary<string, object> { { "path", opened.path }, { "name", opened.name } }));
                        });
                        break;
                    }

                case "POST /scene/save":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            if (EditorApplication.isPlayingOrWillChangePlaymode)
                            {
                                resRespond(res, Fail("Cannot save while entering/exiting play mode."), 409);
                                return;
                            }

                            var scene = SceneManager.GetActiveScene();
                            string p = Json.GetString(bodyDict, "path");
                            if (string.IsNullOrEmpty(p))
                            {
                                if (!scene.IsValid())
                                {
                                    resRespond(res, Fail("No active scene to save."), 400);
                                    return;
                                }
                                if (!EditorSceneManager.SaveScene(scene))
                                {
                                    resRespond(res, Fail("Save failed (scene is read-only?)."), 500);
                                    return;
                                }
                                resRespond(res, Ok(new Dictionary<string, object> { { "path", scene.path } }));
                                return;
                            }

                            if (!p.StartsWith("Assets/", StringComparison.Ordinal)) p = "Assets/" + p;
                            if (!p.EndsWith(".unity", StringComparison.Ordinal)) p += ".unity";
                            var dir = Path.GetDirectoryName(p);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                            if (!EditorSceneManager.SaveScene(scene, p))
                            {
                                resRespond(res, Fail("Save failed for " + p), 500);
                                return;
                            }
                            resRespond(res, Ok(new Dictionary<string, object> { { "path", p } }));
                        });
                        break;
                    }

                case "GET /scene":
                    {
                        var queryDict = ParseQuery(query);
                        int depth = Json.GetInt(queryDict, "depth", 2);
                        bool full = Json.GetBool(queryDict, "full", false);
                        bool showHierarchy = !Json.GetBool(queryDict, "noHierarchy", false);
                        if (depth < 0 || depth > 30) depth = 2;

                        MainThread.Execute(() =>
                        {
                            var scene = SceneManager.GetActiveScene();
                            var roots = scene.IsValid() ? scene.GetRootGameObjects() : new GameObject[0];
                            int total = 0;
                            var rootList = new List<object>();
                            foreach (var r in roots)
                            {
                                if (r == null) continue;
                                total += 1 + CountDescendants(r.transform);
                                if (showHierarchy) rootList.Add(SceneTools.Describe(r, depth, full));
                            }
                            var d = Ok();
                            d["scene"] = scene.IsValid() ? scene.name : null;
                            d["scenePath"] = scene.IsValid() ? scene.path : null;
                            d["isDirty"] = scene.IsValid() && scene.isDirty;
                            d["isPlaying"] = EditorApplication.isPlaying;
                            d["objectCount"] = total;
                            d["rootCount"] = roots.Length;
                            d["roots"] = rootList;
                            resRespond(res, d);
                        });
                        break;
                    }

                case "POST /object/create":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.CreateObject(bodyDict);
                            resRespond(res, Ok(new Dictionary<string, object>
                            {
                                { "object", SceneTools.Describe(go, 0, false) },
                                { "message", "Created \"" + go.name + "\" (" + SceneTools.IdToken(go) + ")" }
                            }));
                        });
                        break;
                    }

                case "POST /object/update":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.Resolve(Locator(bodyDict));
                            SceneTools.UpdateObject(go, bodyDict);
                            resRespond(res, Ok(new Dictionary<string, object>
                            {
                                { "object", SceneTools.Describe(go, 0, false) }
                            }));
                        });
                        break;
                    }

                case "POST /object/delete":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.Resolve(Locator(bodyDict));
                            string n = go.name;
                            SceneTools.DeleteObject(go);
                            resRespond(res, Ok(new Dictionary<string, object> { { "message", "Deleted \"" + n + "\"" } }));
                        });
                        break;
                    }

                case "POST /object/inspect":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.Resolve(Locator(bodyDict));
                            bool includeFields = Json.GetBool(bodyDict, "includeFields", true);
                            var d = Ok();
                            d["object"] = SceneTools.Describe(go, 0, false);
                            d["components"] = includeFields ? SceneTools.Inspect(go) : go.GetComponents<Component>()
                                .Where(c => c != null)
                                .Select(c => (object)new Dictionary<string, object> { { "type", c.GetType().FullName } }).ToList();
                            resRespond(res, d);
                        });
                        break;
                    }

                case "POST /object/select":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.Resolve(Locator(bodyDict));
                            SceneTools.Frame(go);
                            resRespond(res, Ok(new Dictionary<string, object> { { "selected", go.name } }));
                        });
                        break;
                    }

                case "POST /object/find":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            string name = Json.GetString(bodyDict, "name");
                            int limit = Json.GetInt(bodyDict, "limit", 50);
                            var matches = SceneTools.FindAll(name, limit);
                            var d = Ok();
                            d["count"] = matches.Count;
                            d["matches"] = matches.Select(g => (object)new Dictionary<string, object>
                            {
                                { "id", SceneTools.IdToken(g) },
                                { "name", g.name },
                                { "path", SceneTools.GetPath(g) },
                                { "active", g.activeInHierarchy }
                            }).ToList();
                            resRespond(res, d);
                        });
                        break;
                    }

                case "POST /component/add":
                case "POST /component/remove":
                    {
                        var bodyDict = ParseBody(body);
                        bool removing = path.EndsWith("/remove", StringComparison.Ordinal);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.Resolve(Locator(bodyDict));
                            string type = Json.GetString(bodyDict, "type");
                            if (removing)
                            {
                                SceneTools.RemoveComponent(go, type);
                                resRespond(res, Ok(new Dictionary<string, object> { { "message", "Removed " + type + " from " + go.name } }));
                            }
                            else
                            {
                                var c = SceneTools.AddComponent(go, type);
                                resRespond(res, Ok(new Dictionary<string, object> { { "message", "Added " + c.GetType().Name + " to " + go.name } }));
                            }
                        });
                        break;
                    }

                case "POST /component/set":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            var go = SceneTools.Resolve(Locator(bodyDict));
                            var c = SceneTools.FindComponent(go, Json.GetString(bodyDict, "type"));
                            SceneTools.SetComponentField(c, Json.GetString(bodyDict, "field"), bodyDict.ContainsKey("value") ? bodyDict["value"] : null);
                            var updated = SceneTools.ComponentFields(c)
                                .Find(p => string.Equals(p["path"].ToString(), Json.GetString(bodyDict, "field"), StringComparison.Ordinal));
                            resRespond(res, Ok(new Dictionary<string, object> { { "component", c.GetType().Name }, { "field", Json.GetString(bodyDict, "field") }, { "value", updated != null ? updated["value"] : "set (verify via inspect)" } }));
                        });
                        break;
                    }

                case "GET /assets":
                    {
                        var queryDict = ParseQuery(query);
                        MainThread.Execute(() =>
                        {
                            string typeFilter = Json.GetString(queryDict, "type");
                            string nameFilter = Json.GetString(queryDict, "name");
                            int limit = Json.GetInt(queryDict, "limit", 200);
                            if (limit <= 0 || limit > 2000) limit = 200;

                            if (string.IsNullOrEmpty(typeFilter))
                            {
                                var all = new List<Dictionary<string, object>>();
                                foreach (var label in new[] { "Prefab", "Material", "Texture", "Sprite", "Scene", "AnimationClip", "AudioClip", "Shader", "ScriptableObject" })
                                {
                                    var guids = AssetDatabase.FindAssets("t:" + label);
                                    foreach (var g in guids)
                                    {
                                        string p = AssetDatabase.GUIDToAssetPath(g);
                                        if (!p.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                                        all.Add(new Dictionary<string, object> { { "path", p }, { "type", label } });
                                    }
                                    if (all.Count >= limit) break;
                                }
                                resRespond(res, Ok(new Dictionary<string, object> { { "count", all.Count }, { "assets", all } }));
                                return;
                            }

                            string searchFilter = "t:" + typeFilter;
                            var guids2 = AssetDatabase.FindAssets(searchFilter);
                            var matches = new List<Dictionary<string, object>>();
                            foreach (var g in guids2)
                            {
                                string p = AssetDatabase.GUIDToAssetPath(g);
                                if (!p.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                                if (!string.IsNullOrEmpty(nameFilter)
                                    && Path.GetFileNameWithoutExtension(p).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0
                                    && p.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                                matches.Add(new Dictionary<string, object>
                                {
                                    { "path", p },
                                    { "name", Path.GetFileNameWithoutExtension(p) },
                                    { "type", typeFilter }
                                });
                                if (matches.Count >= limit) break;
                            }
                            resRespond(res, Ok(new Dictionary<string, object> { { "count", matches.Count }, { "assets", matches } }));
                        });
                        break;
                    }

                case "POST /prefab/instantiate":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            string prefab = Json.GetString(bodyDict, "prefab");
                            var asset = SceneTools.ResolveAsset<GameObject>(prefab, "Prefab");
                            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                            if (go == null)
                            {
                                resRespond(res, Fail("Failed to instantiate prefab."), 500);
                                return;
                            }

                            if (bodyDict.ContainsKey("parent"))
                            {
                                var parentSpec = bodyDict["parent"] as Dictionary<string, object>;
                                if (parentSpec != null && parentSpec.Count > 0)
                                {
                                    var parentGo = SceneTools.Resolve(parentSpec);
                                    go.transform.SetParent(parentGo.transform, false);
                                }
                            }

                            if (SceneTools.TryVecPublic(bodyDict, "localPosition", "position", out var lv)) go.transform.localPosition = lv;
                            if (SceneTools.TryVecPublic(bodyDict, "localRotation", "rotation", out var lr)) go.transform.localRotation = Quaternion.Euler(lr);
                            if (SceneTools.TryVecPublic(bodyDict, "localScale", "scale", out var ls)) go.transform.localScale = ls;

                            Undo.RegisterCreatedObjectUndo(go, "GPTUnity instantiate");
                            bool sel = Json.GetBool(bodyDict, "select", false);
                            if (sel) Selection.activeGameObject = go;

                            resRespond(res, Ok(new Dictionary<string, object> { { "object", SceneTools.Describe(go, 0, false) } }));
                        });
                        break;
                    }

                case "POST /material/create":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            string matName = Json.GetString(bodyDict, "name");
                            if (string.IsNullOrEmpty(matName))
                            {
                                resRespond(res, Fail("Missing \"name\" for the material."), 400);
                                return;
                            }

                            string dir = "Assets/GPTUnity_Materials";
                            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets", "GPTUnity_Materials");

                            string path = dir + "/" + matName + ".mat";
                            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                            var mat = existing != null ? UnityEngine.Object.Instantiate(existing) : null;
                            if (mat == null)
                            {
                                var shader = Shader.Find("Universal Render Pipeline/Lit") ??
                                              Shader.Find("Standard") ??
                                              Shader.Find("Legacy Shaders/Diffuse");
                                if (shader == null)
                                {
                                    resRespond(res, Fail("No compatible shader found to create a material."), 500);
                                    return;
                                }
                                mat = new Material(shader);
                                AssetDatabase.CreateAsset(mat, path);
                            }
                            else
                            {
                                AssetDatabase.SaveAssets();
                            }

                            ApplyMaterialColor(mat, bodyDict);
                            EditorUtility.SetDirty(mat);
                            AssetDatabase.SaveAssets();

                            resRespond(res, Ok(new Dictionary<string, object> { { "path", path }, { "name", mat.name } }));
                        });
                        break;
                    }

                case "POST /material/set":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            string pf = Json.GetString(bodyDict, "material");
                            var mat = SceneTools.ResolveAsset<Material>(pf, "Material");
                            string property = Json.GetString(bodyDict, "property");
                            string kind = Json.GetString(bodyDict, "type", "color");
                            object raw = bodyDict.ContainsKey("value") ? bodyDict["value"] : null;
                            if (string.IsNullOrEmpty(property))
                            {
                                resRespond(res, Fail("Missing \"property\" (shader property name, e.g. _BaseColor, _Metallic)."), 400);
                                return;
                            }

                            if (mat.HasProperty(property) == false)
                            {
                                resRespond(res, Fail("Property \"" + property + "\" does not exist on shader \"" + mat.shader.name + "\"."), 404);
                                return;
                            }

                            switch (kind.ToLowerInvariant())
                            {
                                case "color":
                                    {
                                        var c = SceneTools.ColorFromValue(Json.Parse(Json.Serialize(raw)));
                                        mat.SetColor(property, c);
                                        EditorUtility.SetDirty(mat);
                                        AssetDatabase.SaveAssets();
                                        resRespond(res, Ok(new Dictionary<string, object> { { "material", AssetDatabase.GetAssetPath(mat) }, { "property", property }, { "value", new[] { (double)c.r, c.g, c.b, c.a } } }));
                                        return;
                                    }
                                case "float":
                                    {
                                        float f = (float)SceneTools.DoubleFromValue(raw, 0);
                                        mat.SetFloat(property, f);
                                        EditorUtility.SetDirty(mat);
                                        AssetDatabase.SaveAssets();
                                        resRespond(res, Ok(new Dictionary<string, object> { { "material", AssetDatabase.GetAssetPath(mat) }, { "property", property }, { "value", (double)f } }));
                                        return;
                                    }
                                case "texture":
                                    {
                                        string texPath = Json.GetString(bodyDict, "value");
                                        var tex = string.IsNullOrEmpty(texPath) ? null : SceneTools.ResolveAsset<Texture>(texPath, "Texture");
                                        mat.SetTexture(property, tex);
                                        EditorUtility.SetDirty(mat);
                                        AssetDatabase.SaveAssets();
                                        resRespond(res, Ok(new Dictionary<string, object> { { "material", AssetDatabase.GetAssetPath(mat) }, { "property", property }, { "value", tex != null ? AssetDatabase.GetAssetPath(tex) : null } }));
                                        return;
                                    }
                                default:
                                    resRespond(res, Fail("Unknown material type \"" + kind + "\". Use color, float or texture."), 400);
                                    return;
                            }
                        });
                        break;
                    }

                case "POST /code/execute":
                    {
                        var bodyDict = ParseBody(body);
                        string code = Json.GetString(bodyDict, "code");
                        if (string.IsNullOrWhiteSpace(code))
                        {
                            resRespond(res, Fail("Missing \"code\". Send the C# that should run inside the Editor."), 400);
                            break;
                        }
                        // Runs on the HTTP thread (no Unity API touches needed until Tick on main thread).
                        var result = CodeExecutor.Execute(code, Json.GetInt(bodyDict, "timeoutSec", 180));
                        if (!result.ok)
                            resRespond(res, OkLogs(result), 422);
                        else
                            resRespond(res, Ok(new Dictionary<string, object>
                            {
                                { "ok", true },
                                { "logs", result.logs },
                                { "result", result.value }
                            }));
                        break;
                    }

                case "POST /play":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            bool on = Json.GetBool(bodyDict, "on", true);
                            if (on && !EditorApplication.isPlaying)
                                EditorApplication.isPlaying = true;
                            if (!on && EditorApplication.isPlaying)
                                EditorApplication.isPlaying = false;
                            resRespond(res, Ok(new Dictionary<string, object> { { "isPlaying", EditorApplication.isPlaying } }));
                        });
                        break;
                    }

                case "POST /pause":
                    {
                        var bodyDict = ParseBody(body);
                        MainThread.Execute(() =>
                        {
                            bool on = Json.GetBool(bodyDict, "on", true);
                            if (EditorApplication.isPlaying) EditorApplication.isPaused = on;
                            resRespond(res, Ok(new Dictionary<string, object> { { "isPaused", EditorApplication.isPaused } }));
                        });
                        break;
                    }

                case "POST /stop":
                    MainThread.Execute(() =>
                    {
                        if (EditorApplication.isPlayingOrWillChangePlaymode)
                            EditorApplication.isPlaying = false;
                        resRespond(res, Ok(new Dictionary<string, object> { { "isPlaying", EditorApplication.isPlaying } }));
                    });
                    break;

                case "POST /server/stop":
                    MainThread.Execute(() =>
                    {
                        Bridge.StopServer();
                        resRespond(res, Ok(new Dictionary<string, object> { { "message", "Server stopped" } }));
                    });
                    break;

                default:
                    resRespond(res, Fail("Unknown route: " + method + " " + path + ". See the OpenAPI spec for all endpoints.", "GET /health and GET /project always work."), 404);
                    break;
            }
        }

        static void ApplyMaterialColor(Material mat, Dictionary<string, object> body)
        {
            if (!body.ContainsKey("color")) return;
            var sim = Json.Serialize(body["color"]);
            var c = SceneTools.ColorFromValue(Json.Parse(sim));
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.Lerp(Color.black, c, 0.15f));
        }

        static Dictionary<string, object> OkLogs(CodeExecutor.ExecutionResult r)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "error", r.error },
                { "logs", r.logs ?? new List<string>() }
            };
        }

        static int CountDescendants(Transform t)
        {
            int n = 0;
            foreach (Transform c in t) n += 1 + CountDescendants(c);
            return n;
        }

        static string ProjectName()
        {
            return Path.GetFileName(Application.dataPath);
        }

        static Dictionary<string, object> Locator(Dictionary<string, object> body)
        {
            if (body.TryGetValue("object", out object inner) && inner is Dictionary<string, object> innerDict)
                return innerDict;
            if (body.TryGetValue("target", out object t) && t is Dictionary<string, object> tDict)
                return tDict;
            return body;
        }

        static Dictionary<string, object> ParseBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return new Dictionary<string, object>();
            var d = Json.Parse(body) as Dictionary<string, object>;
            return d ?? new Dictionary<string, object>();
        }

        static Dictionary<string, object> ParseQuery(string query)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(query)) return result;
            foreach (var pair in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(kv[0]);
                string val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                result[key] = val;
            }
            return result;
        }

        static Dictionary<string, object> Ok(Dictionary<string, object> fields)
        {
            fields["ok"] = true;
            return fields;
        }

        static void resRespond(HttpListenerResponse res, Dictionary<string, object> payload, int code = 200)
        {
            GPTUnityHttpServer.Respond(res, code, Json.Serialize(payload));
        }
    }
}