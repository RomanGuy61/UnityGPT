using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace GPTUnity
{
    /// <summary>Thrown for predictable API errors; routed to a clean HTTP response.</summary>
    public sealed class ApiException : Exception
    {
        public int StatusCode;
        public string Hint;

        public ApiException(string message, int statusCode = 400, string hint = null)
            : base(message)
        {
            StatusCode = statusCode;
            Hint = hint;
        }
    }

    /// <summary>
    /// Core scenes/objects/component/material manipulation helpers. Everything here runs
    /// on the Unity main thread. Prefers Undo-recording so changes are reversible.
    /// </summary>
    public static class SceneTools
    {
        // ---------------- Object identity ----------------
        //
        // Unity 6.4+ moves object identity from the 32-bit int InstanceID to the 64-bit
        // EntityId struct; from Unity 6.5 the old int APIs are compile errors. The bridge
        // exposes a stable session-scoped "id" string token so the JSON contract does not
        // depend on the internal representation. The token round-trips within one Editor
        // session regardless of version.

        public static string IdToken(UnityEngine.Object o)
        {
            if (o == null) return null;
#if UNITY_6000_4_OR_NEWER
            return EntityId.ToULong(o.GetEntityId()).ToString();
#else
            return o.GetInstanceID().ToString();
#endif
        }

        public static UnityEngine.Object ObjectFromIdToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
#if UNITY_6000_4_OR_NEWER
            if (ulong.TryParse(token, out ulong raw))
                return EditorUtility.EntityIdToObject(EntityId.FromULong(raw));
#else
            if (int.TryParse(token, out int instanceId))
                return EditorUtility.InstanceIDToObject(instanceId);
#endif
            return null;
        }

        // ---------------- Locating objects ----------------

        /// <summary>
        /// Accepts a locator dict that may contain any of: "id" (session id token string),
        /// "path" (hierarchy path), "name" (exact name). Called on the main thread.
        /// </summary>
        public static GameObject Resolve(Dictionary<string, object> locator)
        {
            if (locator == null)
                throw new ApiException("Missing object locator. Provide \"id\", \"path\" or \"name\".");

            if (locator.TryGetValue("id", out object idObj) && idObj != null)
            {
                var obj = ObjectFromIdToken(Convert.ToString(idObj, CultureInfo.InvariantCulture));
                if (obj is GameObject go) return go;
            }

            if (locator.TryGetValue("path", out object pathObj) && pathObj is string path && !string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null) return go;
            }

            string name = Json.GetString(locator, "name");
            if (!string.IsNullOrEmpty(name))
            {
                var go = GameObject.Find(name);
                if (go != null) return go;

                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                GameObject fallback = null;
                foreach (var g in all)
                {
                    if (g == null || !g.scene.IsValid() || !g.scene.isLoaded) continue;
                    if (g.name == name)
                    {
                        if (g.isActiveInHierarchy) return g;
                        if (fallback == null) fallback = g;
                    }
                }
                if (fallback != null) return fallback;

                foreach (var g in all)
                {
                    if (g == null || !g.scene.IsValid() || !g.scene.isLoaded) continue;
                    if (g.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (g.isActiveInHierarchy) return g;
                        if (fallback == null) fallback = g;
                    }
                }
                if (fallback != null) return fallback;
            }

            throw new ApiException("Object not found. Tried id/path/name: " + Json.Serialize(locator), 404);
        }

        public static GameObject FindByPath(string hierarchyPath)
        {
            string[] parts = hierarchyPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (string.Equals(root.name, parts[0], StringComparison.Ordinal))
                {
                    GameObject current = root;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        Transform child = null;
                        foreach (Transform c in current.transform)
                        {
                            if (string.Equals(c.name, parts[i], StringComparison.Ordinal))
                            {
                                child = c;
                                break;
                            }
                        }
                        if (child == null) return null;
                        current = child.gameObject;
                    }
                    return current;
                }
            }
            return null;
        }

        public static List<GameObject> FindAll(string name, int limit)
        {
            var result = new List<GameObject>();
            if (string.IsNullOrEmpty(name)) return result;
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (var g in all)
            {
                if (g == null || !g.scene.IsValid() || !g.scene.isLoaded) continue;
                if (string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase)
                    || g.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    g.name == name)
                {
                    result.Add(g);
                    if (result.Count >= limit) break;
                }
            }
            return result;
        }

        public static string GetPath(GameObject go)
        {
            var sb = new StringBuilder(go.name);
            var parent = go.transform.parent;
            while (parent != null)
            {
                sb.Insert(0, parent.name + "/");
                parent = parent.parent;
            }
            return sb.ToString();
        }

        // ---------------- Describing ----------------

        public static Dictionary<string, object> Describe(GameObject go, int depth, bool full)
        {
            return DescribeNode(go, depth, full, 0);
        }

        static Dictionary<string, object> DescribeNode(GameObject go, int maxDepth, bool full, int depth)
        {
            var d = new Dictionary<string, object>();
            d["id"] = IdToken(go);
            d["name"] = go.name;
            d["active"] = go.activeSelf;
            d["activeInHierarchy"] = go.isActiveInHierarchy;
            d["isStatic"] = go.isStatic;
            d["tag"] = go.tag;
            d["layer"] = go.layer;
            d["layerName"] = LayerMask.LayerToName(go.layer);
            d["path"] = GetPath(go);

            var t = go.transform;
            var p = t.localPosition;
            var r = t.localRotation.eulerAngles;
            var s = t.localScale;
            d["localPosition"] = new[] { p.x, p.y, p.z };
            d["localRotation"] = new[] { r.x, r.y, r.z };
            d["localScale"] = new[] { s.x, s.y, s.z };
            var wp = t.position;
            d["position"] = new[] { wp.x, wp.y, wp.z };

            if (go.transform.parent != null)
            {
                d["parent"] = new Dictionary<string, object>
                {
                    { "id", IdToken(go.transform.parent.gameObject) },
                    { "name", go.transform.parent.name }
                };
            }

            var rend = go.GetComponent<Renderer>();
            if (rend != null && rend.bounds.size != Vector3.zero)
            {
                d["boundsCenter"] = new[] { rend.bounds.center.x, rend.bounds.center.y, rend.bounds.center.z };
                d["boundsSize"] = new[] { rend.bounds.size.x, rend.bounds.size.y, rend.bounds.size.z };
            }

            var comps = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                comps.Add(c == null ? "(missing)" : c.GetType().Name);
            d["components"] = comps;

            if (full)
            {
                var fields = new Dictionary<string, object>();
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    fields[c.GetType().FullName] = ComponentFields(c);
                }
                d["componentFields"] = fields;
            }

            if (depth < maxDepth)
            {
                var children = new List<object>();
                foreach (Transform child in go.transform)
                    children.Add(DescribeNode(child.gameObject, maxDepth, full, depth + 1));
                d["children"] = children;
            }

            return d;
        }

        public static List<Dictionary<string, object>> ComponentFields(Component c)
        {
            var result = new List<Dictionary<string, object>>();
            if (c == null) return result;
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.depth != 0) continue;
                if (string.Equals(it.name, "m_Script", StringComparison.Ordinal)) continue;
                result.Add(PropertyDump(it));
            }
            return result;
        }

        static Dictionary<string, object> PropertyDump(SerializedProperty p)
        {
            var d = new Dictionary<string, object>();
            d["path"] = p.propertyPath;
            d["name"] = p.displayName;
            d["type"] = p.propertyType.ToString();

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: d["value"] = p.intValue; break;
                case SerializedPropertyType.Boolean: d["value"] = p.boolValue; break;
                case SerializedPropertyType.Float: d["value"] = p.floatValue; break;
                case SerializedPropertyType.String: d["value"] = p.stringValue; break;
                case SerializedPropertyType.Enum: d["value"] = p.enumNames.Length > p.enumValueIndex ? p.enumNames[p.enumValueIndex] : p.enumValueIndex.ToString(); break;
                case SerializedPropertyType.Color:
                    {
                        var cv = p.colorValue;
                        d["value"] = new[] { (double)cv.r, cv.g, cv.b, cv.a };
                        break;
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var v = p.vector2Value;
                        d["value"] = new[] { (double)v.x, v.y };
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var v = p.vector3Value;
                        d["value"] = new[] { (double)v.x, v.y, v.z };
                        break;
                    }
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                    {
                        var v = p.vector4Value;
                        d["value"] = new[] { (double)v.x, v.y, v.z, v.w };
                        break;
                    }
                case SerializedPropertyType.Vector2Int:
                    d["value"] = new[] { (double)p.vector2IntValue.x, p.vector2IntValue.y }; break;
                case SerializedPropertyType.Vector3Int:
                    d["value"] = new[] { (double)p.vector3IntValue.x, p.vector3IntValue.y, p.vector3IntValue.z }; break;
                case SerializedPropertyType.ObjectReference:
                    d["value"] = p.objectReferenceValue == null ? null : p.objectReferenceValue.name;
                    d["assetPath"] = AssetDatabase.GetAssetPath(p.objectReferenceValue) ?? "";
                    break;
                case SerializedPropertyType.ArraySize:
                    d["value"] = p.intValue;
                    break;
                case SerializedPropertyType.LayerMask:
                    d["value"] = p.intValue;
                    break;
                default:
                    d["value"] = p.type;
                    break;
            }
            return d;
        }

        // ---------------- Mutations ----------------

        public static GameObject CreateObject(Dictionary<string, object> body)
        {
            string name = Json.GetString(body, "name", "GPT Object");
            string primitive = Json.GetString(body, "primitive");
            bool isEmpty = Json.GetBool(body, "empty", false);
            string prefabPath = Json.GetString(body, "prefab");

            GameObject go;
            if (!string.IsNullOrEmpty(prefabPath))
            {
                var asset = ResolveAsset<GameObject>(prefabPath, "Prefab");
                go = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                if (go == null) throw new ApiException("Failed to instantiate prefab: " + prefabPath);
            }
            else if (!string.IsNullOrEmpty(primitive))
            {
                if (!Enum.TryParse(primitive, true, out PrimitiveType pt))
                    throw new ApiException("Unknown primitive \"" + primitive + "\". Use Cube, Sphere, Capsule, Cylinder, Plane, Quad.");
                go = GameObject.CreatePrimitive(pt);
            }
            else if (isEmpty)
            {
                go = new GameObject(name);
            }
            else
            {
                go = new GameObject(name);
            }

            go.name = name;

            if (body.TryGetValue("parent", out object parentSpec))
            {
                Dictionary<string, object> pd = parentSpec as Dictionary<string, object>;
                if (pd == null && parentSpec is string pname && !string.IsNullOrEmpty(pname))
                    pd = new Dictionary<string, object> { { "name", pname } };
                if (pd != null && pd.Count > 0)
                {
                    var parentGo = Resolve(pd);
                    go.transform.SetParent(parentGo.transform, false);
                }
            }
            else
            {
                // allow "parentPath" convenience
                string ppath = Json.GetString(body, "parentPath");
                if (!string.IsNullOrEmpty(ppath))
                {
                    var parentGo = FindByPath(ppath);
                    if (parentGo == null) throw new ApiException("parentPath not found: " + ppath);
                    go.transform.SetParent(parentGo.transform, false);
                }
            }

            ForEachVec(body, "localPosition", "worldPosition", "position", v => go.transform.localPosition = v, null);
            ForEachVec(body, "localRotation", "worldRotation", "rotation", v => go.transform.localRotation = Quaternion.Euler(v), null);
            ForEachVec(body, "localScale", "scale", null, v => go.transform.localScale = v, null);

            Undo.RegisterCreatedObjectUndo(go, "GPTUnity create " + name);

            if (Json.GetBool(body, "select", false))
                Selection.activeGameObject = go;

            return go;
        }

        static void ForEachVec(Dictionary<string, object> body, string localKey, string worldKey, string legacyKey,
            Action<Vector3> localSetter, Action<Vector3> worldSetter)
        {
            if (worldKey != null && worldSetter != null && TryVec(body, worldKey, out Vector3 wv))
            {
                worldSetter(wv);
                return;
            }
            if (TryVec(body, localKey, out Vector3 lv))
            {
                localSetter(lv);
                return;
            }
            if (legacyKey != null && TryVec(body, legacyKey, out Vector3 lg))
                localSetter(lg);
        }

        static bool TryVec(Dictionary<string, object> body, string key, out Vector3 v)
        {
            v = default;
            var l = Json.GetList(body, key);
            if (l == null) return false;
            if (l.Count < 1) return false;
            double x = l[0] is long xl ? xl : Convert.ToDouble(l[0], CultureInfo.InvariantCulture);
            double y = l.Count > 1 ? (l[1] is long yl ? yl : Convert.ToDouble(l[1], CultureInfo.InvariantCulture)) : 0;
            double z = l.Count > 2 ? (l[2] is long zl ? zl : Convert.ToDouble(l[2], CultureInfo.InvariantCulture)) : 0;
            v = new Vector3((float)x, (float)y, (float)z);
            return true;
        }

        public static void UpdateObject(GameObject go, Dictionary<string, object> body)
        {
            bool changed = false;

            if (body.ContainsKey("name"))
            {
                Undo.RecordObject(go, "GPTUnity rename");
                go.name = Json.GetString(body, "name", go.name);
                changed = true;
            }
            if (body.ContainsKey("active"))
            {
                Undo.RecordObject(go, "GPTUnity active");
                go.SetActive(Json.GetBool(body, "active", go.activeSelf));
                changed = true;
            }
            if (body.ContainsKey("tag"))
            {
                string tag = Json.GetString(body, "tag");
                try
                {
                    Undo.RecordObject(go, "GPTUnity tag");
                    go.tag = tag;
                    changed = true;
                }
                catch (Exception ex)
                {
                    throw new ApiException("Invalid or missing tag \"" + tag + "\". Add it in Project Settings > Tags and Layers. (" + ex.Message + ")");
                }
            }
            if (body.ContainsKey("layer") || body.ContainsKey("layerName"))
            {
                int layer = go.layer;
                if (body.ContainsKey("layer")) layer = Json.GetInt(body, "layer", layer);
                else
                {
                    string ln = Json.GetString(body, "layerName");
                    layer = LayerMask.NameToLayer(ln);
                    if (layer < 0) throw new ApiException("Unknown layer name \"" + ln + "\".");
                }
                Undo.RecordObject(go, "GPTUnity layer");
                go.layer = layer;
                changed = true;
            }

            var t = go.transform;
            Vector3 wp = default, lp = default;
            bool wr = TryVec(body, "worldPosition", out wp);
            bool hasLocalPos = TryVec(body, "localPosition", out lp);
            bool hasPosition = TryVec(body, "position", out Vector3 posV);
            if (wr) { Undo.RecordObject(t, "GPTUnity position"); t.position = wp; changed = true; }
            else if (hasLocalPos) { Undo.RecordObject(t, "GPTUnity position"); t.localPosition = lp; changed = true; }
            else if (hasPosition) { Undo.RecordObject(t, "GPTUnity position"); t.localPosition = posV; changed = true; }

            bool rot = TryVec(body, "worldRotation", out Vector3 wrot);
            bool lrot = TryVec(body, "localRotation", out Vector3 lrotV) || TryVec(body, "rotation", out lrotV);
            if (rot) { Undo.RecordObject(t, "GPTUnity rotation"); t.rotation = Quaternion.Euler(wrot); changed = true; }
            else if (lrot) { Undo.RecordObject(t, "GPTUnity rotation"); t.localRotation = Quaternion.Euler(lrotV); changed = true; }

            bool wsc = TryVec(body, "worldScale", out Vector3 wscV);
            bool lsc = TryVec(body, "localScale", out Vector3 lscV) || TryVec(body, "scale", out lscV);
            if (wsc) { Undo.RecordObject(t, "GPTUnity scale"); t.localScale = KeepLocalScale(t, wscV); changed = true; }
            else if (lsc) { Undo.RecordObject(t, "GPTUnity scale"); t.localScale = lscV; changed = true; }

            if (!changed)
                throw new ApiException("Nothing to update. Send name/active/tag/layer/position/rotation/scale.");
        }

        static Vector3 KeepLocalScale(Transform t, Vector3 worldScale)
        {
            if (t.parent == null) return worldScale;
            var ps = t.parent.lossyScale;
            return new Vector3(
                ps.x != 0 ? worldScale.x / ps.x : worldScale.x,
                ps.y != 0 ? worldScale.y / ps.y : worldScale.y,
                ps.z != 0 ? worldScale.z / ps.z : worldScale.z);
        }

        public static void DeleteObject(GameObject go)
        {
            if (go == null) return;
            Undo.DestroyObjectImmediate(go);
        }

        public static List<Dictionary<string, object>> Inspect(GameObject go)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var d = new Dictionary<string, object>();
                d["type"] = c.GetType().FullName;
                d["typeName"] = c.GetType().Name;
                d["enabled"] = c is Behaviour b ? b.enabled : true;
                d["properties"] = ComponentFields(c);
                result.Add(d);
            }
            return result;
        }

        // ---------------- Components ----------------

        public static Component FindComponent(GameObject go, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) throw new ApiException("Missing \"type\".");
            var t = TypeHelper.FindType(typeName);
            if (t == null) throw new ApiException("No Component type found for \"" + typeName + "\".");
            var c = go.GetComponent(t);
            if (c == null) throw new ApiException("Object \"" + go.name + "\" has no " + t.Name + " component.", 404,
                "Use /object/inspect to see its components, or /component/add to add one.");
            return c;
        }

        public static Component AddComponent(GameObject go, string typeName)
        {
            var t = TypeHelper.FindType(typeName);
            if (t == null) throw new ApiException("No Component type found for \"" + typeName + "\".");
            if (go.GetComponent(t) != null) throw new ApiException("Object already has a " + t.Name + " component.");
            if (!typeof(Component).IsAssignableFrom(t))
                throw new ApiException(t.Name + " is not a Component, so it cannot be added.");
            return Undo.AddComponent(go, t);
        }

        public static void RemoveComponent(GameObject go, string typeName)
        {
            var c = FindComponent(go, typeName);
            Undo.DestroyObjectImmediate(c);
        }

        public static void SetComponentField(Component c, string fieldPath, object rawValue)
        {
            if (string.IsNullOrEmpty(fieldPath))
                throw new ApiException("Missing \"field\" (the SerializedProperty path from /object/inspect).");

            var so = new SerializedObject(c);
            var sp = so.FindProperty(fieldPath);
            if (sp == null)
                throw new ApiException("Field \"" + fieldPath + "\" does not exist on " + c.GetType().Name + ".", 404,
                    "Read /object/inspect and copy an exact property \"path\" value.");

            Undo.RecordObject(c, "GPTUnity set " + fieldPath);

            var json = Json.Serialize(rawValue);
            var simplified = Json.Parse(json);

            if (simplified is List<object> flat && flat.Count == 1)
                simplified = flat[0];
            if (simplified is Dictionary<string, object> d && d.Count == 1)
                simplified = d["value"] ?? simplified;

            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer:
                    sp.intValue = ToInt(simplified, sp.intValue);
                    break;
                case SerializedPropertyType.Float:
                    sp.floatValue = (float)ToDouble(simplified, sp.floatValue);
                    break;
                case SerializedPropertyType.Boolean:
                    sp.boolValue = ToBool(simplified, sp.boolValue);
                    break;
                case SerializedPropertyType.String:
                    sp.stringValue = simplified?.ToString() ?? "";
                    break;
                case SerializedPropertyType.Enum:
                    {
                        string str = simplified?.ToString();
                        if (str != null && int.TryParse(str, out int enumIdx)) sp.enumValueIndex = enumIdx;
                        else
                        {
                            int idx = Array.IndexOf(sp.enumNames, str);
                            if (idx < 0) throw new ApiException("Unknown enum value \"" + str + "\". Options: " + string.Join(", ", sp.enumNames));
                            sp.enumValueIndex = idx;
                        }
                        break;
                    }
                case SerializedPropertyType.Color:
                    sp.colorValue = ToColor(simplified, sp.colorValue);
                    break;
                case SerializedPropertyType.Vector2:
                    sp.vector2Value = ToVector2(simplified, sp.vector2Value);
                    break;
                case SerializedPropertyType.Vector3:
                    sp.vector3Value = ToVector3(simplified, sp.vector3Value);
                    break;
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                    sp.vector4Value = ToVector4(simplified, sp.vector4Value);
                    break;
                case SerializedPropertyType.LayerMask:
                    sp.intValue = ToInt(simplified, sp.intValue);
                    break;
                default:
                    throw new ApiException("Field type " + sp.propertyType + " is not settable through this endpoint at " + fieldPath + ". Use /code/execute for advanced cases.");
            }

            so.ApplyModifiedProperties();
        }

        static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            if (v is long l) return (int)l;
            if (v is bool b) return b ? 1 : 0;
            return int.TryParse(v.ToString(), out int n) ? n : fallback;
        }

        static double ToDouble(object v, double fallback)
        {
            if (v == null) return fallback;
            if (v is long l) return l;
            if (v is double dd) return dd;
            return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallback;
        }

        static bool ToBool(object v, bool fallback)
        {
            if (v == null) return fallback;
            if (v is bool b) return b;
            if (v is long l) return l != 0;
            string s = v.ToString();
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        static Color ToColor(object v, Color fallback)
        {
            if (v is List<object> l)
            {
                if (l.Count < 3) return fallback;
                double r = ToDouble(l[0], fallback.r), g = ToDouble(l[1], fallback.g), b = ToDouble(l[2], fallback.b);
                double a = l.Count > 3 ? ToDouble(l[3], fallback.a) : fallback.a;
                return new Color((float)r, (float)g, (float)b, (float)a);
            }
            if (v is string s && s.StartsWith("#") && ColorUtility.TryParseHtmlString(s, out Color c))
                return c;
            return fallback;
        }

        static Vector2 ToVector2(object v, Vector2 fb)
        {
            if (v is List<object> l && l.Count >= 2)
                return new Vector2((float)ToDouble(l[0], fb.x), (float)ToDouble(l[1], fb.y));
            return fb;
        }

        static Vector3 ToVector3(object v, Vector3 fb)
        {
            if (v is List<object> l && l.Count >= 3)
                return new Vector3((float)ToDouble(l[0], fb.x), (float)ToDouble(l[1], fb.y), (float)ToDouble(l[2], fb.z));
            return fb;
        }

        static Vector4 ToVector4(object v, Vector4 fb)
        {
            if (v is List<object> l && l.Count >= 4)
                return new Vector4((float)ToDouble(l[0], fb.x), (float)ToDouble(l[1], fb.y), (float)ToDouble(l[2], fb.z), (float)ToDouble(l[3], fb.w));
            return fb;
        }

        // ---------------- Assets ----------------

        public static T ResolveAsset<T>(string pathOrName, string kindLabel) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(pathOrName))
                throw new ApiException("Missing " + kindLabel + " path/name.");

            var asset = AssetDatabase.LoadAssetAtPath<T>(pathOrName);
            if (asset != null) return asset;

            if (asset == null)
            {
                var guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
                foreach (var g in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    string name = System.IO.Path.GetFileNameWithoutExtension(p);
                    if (name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase) || p.IndexOf(pathOrName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        asset = AssetDatabase.LoadAssetAtPath<T>(p);
                        if (asset != null) break;
                    }
                }
            }

            if (asset == null)
                throw new ApiException("No " + kindLabel + " found at/under \"" + pathOrName + "\". Use /assets to search.", 404);
            return asset;
        }

        public static void Frame(GameObject go)
        {
            Selection.activeGameObject = go;
            if (SceneView.currentDrawingSceneView != null && go != null)
            {
                SceneView.currentDrawingSceneView.FrameSelected(false);
                SceneView.currentDrawingSceneView.Repaint();
            }
        }

        // Public helpers reused by the API router and the GPT-facing layer.
        public static bool TryVecPublic(Dictionary<string, object> body, string keyA, string keyB, out Vector3 v)
        {
            if (keyB != null && TryVec(body, keyB, out v)) return true;
            return TryVec(body, keyA, out v);
        }

        public static Color ColorFromValue(object simplified)
        {
            if (simplified is List<object> l && l.Count >= 3)
            {
                double r = Convert.ToDouble(l[0], CultureInfo.InvariantCulture);
                double g = Convert.ToDouble(l[1], CultureInfo.InvariantCulture);
                double b = Convert.ToDouble(l[2], CultureInfo.InvariantCulture);
                double a = l.Count > 3 ? Convert.ToDouble(l[3], CultureInfo.InvariantCulture) : 1;
                return new Color((float)r, (float)g, (float)b, (float)a);
            }
            if (simplified is string str)
            {
                if (ColorUtility.TryParseHtmlString(str, out Color parsed)) return parsed;
                switch (str.ToLowerInvariant())
                {
                    case "white": return Color.white;
                    case "black": return Color.black;
                    case "red": return Color.red;
                    case "green": return Color.green;
                    case "blue": return Color.blue;
                    case "yellow": return Color.yellow;
                    case "cyan": return Color.cyan;
                    case "magenta": return Color.magenta;
                    case "gray":
                    case "grey": return Color.gray;
                }
            }
            return Color.white;
        }

        public static double DoubleFromValue(object raw, double fallback)
        {
            if (raw == null) return fallback;
            if (raw is long l) return l;
            if (raw is double d) return d;
            return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallback;
        }
    }

    /// <summary>Resolves type names across all loaded assemblies (for component add/set).</summary>
    public static class TypeHelper
    {
        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = name.Trim();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == null) continue;
                var t = asm.GetType(name, false);
                if (t != null) return t;
            }
            if (!name.Contains("."))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    foreach (var t in SafeTypes(asm))
                    {
                        if (t == null) continue;
                        if (t.Name == name && typeof(Component).IsAssignableFrom(t)) return t;
                    }
                }
            }
            return null;
        }

        static IEnumerable<Type> SafeTypes(System.Reflection.Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types ?? Type.EmptyTypes; }
            catch { return Type.EmptyTypes; }
        }
    }
}