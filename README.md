# GPTUnity Bridge

A Unity Editor package that exposes a **token-authenticated local HTTP/JSON API** so a custom ChatGPT GPT (desktop app, Actions) can inspect and edit your Unity project: scenes, GameObjects, components, materials, assets, and play mode — plus **arbitrary C# execution** inside the Editor for anything the typed endpoints can't express.

The bridge runs **only on your machine** (`http://127.0.0.1:8765` by default), binds to loopback only, and requires an `X-Auth-Token`. Nothing leaves your computer; the chat requests arrive at your local HTTP server directly.

---

## What it does

- **Read** the open scene (hierarchy dump with depth control), find/inspect objects, list assets.
- **Write** objects: create primitives/empty GameObjects, move/rotate/scale, rename, reparent, delete, select/frame.
- **Components**: add, remove, and set serialized fields (`m_Speed`, `m_IsTrigger`, `m_Materials.Array.data[0]`, ...) by path.
- **Prefabs & materials**: instantiate prefabs, create/edit materials (color/float/texture properties).
- **Scenes**: open/save scenes (with `Assets/`-relative paths), list all scenes in the project.
- **Play mode**: enter/pause/stop.
- **Arbitrary code**: `POST /code/execute` compiles your C# into a dedicated Editor-only assembly and runs it on the main thread, returning logs and a result.

---

## Architecture

```
ChatGPT (custom GPT + Action)
   │  HTTPS (OpenAI-hosted Action server)
   ▼
localhost HTTP requests ──► Unity Editor (HttpListener, background thread)
                                 │  MainThread.Execute(...)
                                 ▼
                         Unity API (main thread)
                                 │
                         responses → JSON → chat
```

- The HTTP listener runs on background threads; every Unity API call is marshaled to the main thread (`MainThread.Execute`).
- `/code/execute` writes a generated `Assets/GPTUnity_Generated_Bridge/` assembly (Editor-only). Writing a script triggers a **domain reload**; the pending job survives via `SessionState` and results are handed back through a temp file that the HTTP thread polls — so the request survives the reload too.
- The generated bridge folder is git-ignored and stays in the project to make repeat calls cheap (only that one assembly recompiles).

---

## Install

Works with Unity 2021.3+. Install as a package via the Package Manager:

1. Open your project in Unity and go to **Window > Package Manager**.
2. Click **+** → **Add package from git URL...**.
3. Paste the URL of this repository (e.g. `https://github.com/yourname/GPTUnity.git`) and click **Add**.

Alternatively, copy the `Editor/` folder into your project's `Assets/` and create a parent folder named `Editor` yourself — but git URL install is cleaner.

> The package is **Editor-only**. It never ships in builds.

---

## Setup (in Unity)

1. **Window > GPTUnity Bridge** to open the control window.
2. Copy the **token** (and optionally set a port, default `8765`).
3. Click **Start server**. The window shows the base URL (`http://127.0.0.1:8765`).
4. Click **Ping** to confirm reachability. The status column shows `Server: running (127.0.0.1:8765)`.

Settings (token, port, auto-start) are persisted in the editor and re-applied on load.

---

## Connect ChatGPT

1. Go to **https://chatgpt.com/gpts/editor/** → **Configure**.
2. Under **Actions**, click **Create new action** and **Import from URL / paste JSON**: paste the contents of [`chat/unity-gpt.openapi.json`](chat/unity-gpt.openapi.json).
3. In the **Authentication** section, select **API Key**, type `X-Auth-Token`, paste your Unity token, and choose **Header**.
4. Paste the contents of [`chat/unity-gpt-instructions.md`](chat/unity-gpt-instructions.md) as an instruction block in the GPT's **Instructions** field (or as a second instruction file).
5. Ensure **Privacy: open the custom GPT to external API** is enabled, then **Save and publish**.

### Localhost caveat

The hostname is `127.0.0.1` — that only works if ChatGPT executes Actions on **your machine**. That is the case for the official **ChatGPT desktop app** with Actions (the app resolves `localhost` to the calling machine). For web-only usage you would need a public tunnel; see [HTTPS / remote access](#https--remote-access), below.

---

## Endpoints

All request/response bodies are JSON. Errors come back as `{"ok":false,"error":"...", "hint":"..."}` with an appropriate HTTP status.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | Service + project + Unity version + play state (always unauthenticated-safe). |
| `GET` | `/status` | Server running state, port, URL. |
| `GET` | `/project` | Project name, data path, build target, build settings scenes. |
| `GET` | `/scenes` | List all scenes with paths. |
| `POST` | `/scene/open` | Open a scene (`{"path":"Assets/Scenes/Main.unity"}`). |
| `POST` | `/scene/save` | Save active scene (`{"path":...}` optional for new scene). |
| `GET` | `/scene` | Active scene dump. Query: `depth` (0–30), `full`, `noHierarchy`. |
| `POST` | `/object/create` | Create primitive/empty/prefab; set name, parent, transforms. |
| `POST` | `/object/update` | Rename, active/tag/layer, local & world position/rotation/scale. |
| `POST` | `/object/delete` | Delete an object. |
| `POST` | `/object/inspect` | Object + component fields (`includeFields`). |
| `POST` | `/object/select` | Select + frame in the editor. |
| `POST` | `/object/find` | Find by name (partial), returns id/name/path/active. |
| `POST` | `/component/add` | Add a component by type name (e.g. `Rigidbody`). |
| `POST` | `/component/remove` | Remove a component by type name. |
| `POST` | `/component/set` | Set one serialized field by path (`type`, `field`, `value`). |
| `GET` | `/assets` | List assets by `type` (Prefab/Material/Texture/Scene/...), `name`, `limit`. |
| `POST` | `/prefab/instantiate` | Instantiate a prefab (`prefab` path or name, `parent`, transforms, `select`). |
| `POST` | `/material/create` | Create a material with an optional `color`. |
| `POST` | `/material/set` | Set a shader property (`type`: `color`/`float`/`texture`). |
| `POST` | `/code/execute` | Run arbitrary C# in the Editor (see below). |
| `POST` | `/play` | `{"on":true/false}` enter/leave play mode. |
| `POST` | `/pause` | `{"on":true/false}` pause/resume. |
| `POST` | `/stop` | Stop play mode. |
| `POST` | `/server/stop` | Stop the bridge server. |

**Object locators**: `/object/*`, `/component/*` and `/prefab/instantiate` accept the target under `object` (or `target`) as `{"id":123}` (instance id), `{"name":"Cube"}`, or `{"path":"Room/Tables/Table_A"}`.

**Coordinates** are local by default (`localPosition`, `localRotation` in Euler degrees, `localScale` as `[x,y,z]`); use `worldPosition` when needed.

---

## `/code/execute`

```json
POST /http://127.0.0.1:8765/code/execute
{
  "code": "var go = GPT.Find(\"Player\"); if (go != null) { GPT.Log(\"Found at \" + go.transform.position); GPT.Result(go.transform.position); } else GPT.Result(\"not found\");",
  "timeoutSec": 180
}
```

The `code` string becomes the body of a static `Run()` method executed on the Unity main thread. Helper APIs:

- `GPT.Log(object)` — append to `logs`.
- `GPT.Result(object)` — set the returned `result`.
- `GPT.Find(name)` / `GPT.FindAll(name)` — scene-object search helpers.
- `GPT.Select(GameObject)`, `GPT.Destroy(GameObject)`, `GPT.SetDirty(Object)`.

You can use the full `UnityEngine.*` / `UnityEditor.*` API. Compilation takes a few seconds and triggers a domain reload; the request blocks until your code has run (default 180 s), then returns:

```json
{ "ok": true, "logs": [...], "result": <value> }
```

---

## Security

- The server binds **only** to `127.0.0.1` and `localhost` — it is not reachable over the network.
- Every request requires the `X-Auth-Token` header (32-hex, generated per editor). Requests without it get `401`.
- `/code/execute` can run **arbitrary code with full editor privileges**. Use it only with tokens/links you trust; anyone with the token can modify your project and run code as you.
- Do not share your token, do not paste it into chat prompts.

### HTTPS / remote access

ChatGPT's web UI executes Actions from OpenAI's servers and cannot reach your `127.0.0.1`. Two options:

- **Desktop app** (recommended): the ChatGPT desktop app resolves `localhost` to your machine, so the Action works end-to-end with no extra infrastructure.
- **Tunnel** for web use: run a public tunnel to `${BASE}` (e.g. an HTTPS reverse proxy to your machine) and change the server URLs in the OpenAPI file to the tunnel URL. You should additionally enforce an allow-list / IP restriction on the tunnel. This exposes your Editor to the internet — use a freshly generated token and never over plain SSH tunnels that leak traffic.

---

## Troubleshooting

- **`401` on every call** → wrong/missing token. Copy it from **Window > GPTUnity Bridge** and re-import it in the Action's Authentication.
- **Server not running / connection refused** → open the window, press **Start server**, then **Ping**.
- **`/code/execute` returns 422 with compile errors** → the generated bridge failed to compile; the errors are returned in `error`. Fix the C# and retry.
- **`/scene` too big** → lower `depth`, or `noHierarchy=true`.
- **Field not settable via `/component/set`** → inspect first to get the exact `SerializedProperty` path; if it still won't set (custom type), use `/code/execute`.

---

## Limitations

- Editor-only — no effect on builds.
- A code execution forces an editor domain reload (a few seconds); avoid calling it in a tight loop.
- `/code/execute` mutations are not undoable.
- Changing scenes/objects requires the project to be open in the same editor session that owns the token.

---

## Use with Codex

The repo doubles as a **Codex marketplace** so Codex agents can get a skill for driving a locally
running Unity Editor through the bridge:

```bash
codex mkt add /home/Roman-Bazzite/Documents/GPTUnity
```

The marketplace provides one plugin (`gptunity-bridge`) with the `gptunity-bridge` skill. After
adding, enable it in the Codex plugins UI (or `[plugins."gptunity-bridge@gptunity"] enabled = true`
in `~/.codex/config.toml`). The skill loads the same workflow the ChatGPT GPT uses: health check,
read before writing, typed endpoints first, `/code/execute` as the escape hatch.

---

## Files

```
Editor/
  GPTUnityBridge.cs          server lifecycle, token, port, logging
  GPTUnityHttpServer.cs      HttpListener, auth, CORS, error envelope
  GPTUnityApi.cs             route table (all endpoints)
  GPTUnitySceneTools.cs      object/component/material helpers
  GPTUnityCodeExecutor.cs    /code/execute pipeline + domain-reload bridge
  GPTUnityJson.cs            dependency-free JSON parser/serializer
  GPTUnityMainThread.cs      main-thread dispatcher
  GPTUnityBridgeWindow.cs    Window > GPTUnity Bridge UI
.claude-plugin/
  marketplace.json           Codex/Claude marketplace manifest (gptunity)
codex/
  .codex-plugin/plugin.json  codex plugin manifest
  skills/gptunity-bridge/    SKILL.md workbook for Codex agents
chat/
  unity-gpt.openapi.json     OpenAPI 3.0.3 schema for the ChatGPT Action
  unity-gpt-instructions.md  copy-paste instruction block for the GPT
package.json                 UPM package manifest (com.gptunity.bridge)
```

## License

MIT — see [LICENSE](LICENSE).