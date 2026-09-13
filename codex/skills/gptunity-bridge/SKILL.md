---
name: gptunity-bridge
description: The user is working with a Unity Editor running the GPTUnity Bridge package on this machine. Load this skill whenever the task involves editing the Unity scene, GameObjects, components, materials, prefabs, assets, play mode, or running C# inside the Unity Editor. Use it to inspect and change the user's Unity project through the bridge's local HTTP API.
---

# GPTUnity Bridge

GPTUnity Bridge is a token-authenticated local HTTP server that runs inside the Unity Editor
(`http://127.0.0.1:8765` by default). It lets you read and edit the open Unity project: scenes,
GameObjects, components, materials, assets, play mode, plus arbitrary C# execution via
`/code/execute`. The server is localhost-only and survives only while the Unity Editor is open.

## Prerequisites — ask the user

1. The Unity project must be open in the Editor.
2. The user must open **Window > GPTUnity Bridge** and press **Start server** (or it auto-started,
   `GPTUnity.AutoStart`). 
3. You need the **token**. Ask the user to copy it from the GPTUnity Bridge window, then use it in
   the `X-Auth-Token` header of every request. Treat the token as a secret: do not log it, do not
   put it in files, and do not echo it. If the user would rather not paste it into chat, they can
   set it in an environment variable (e.g. `GPTUNITY_TOKEN`) that your shell has access to.

If `/health` is unreachable, tell the user to (re)start the server in the Unity window and retry.

## Calling the API

Use `curl` (or any HTTP tool) with the token:

```bash
TOKEN="${GPTUNITY_TOKEN:-PASTE_TOKEN_HERE}"
curl -s -H "X-Auth-Token: $TOKEN" http://127.0.0.1:8765/health
```

Successful responses use `{"ok": true, ...}`; errors use `{"ok": false, "error": "...", "hint": "..."}`.

## Workflow

1. Always start with `GET /health` — record `projectName`, `activeScene`, `isPlaying`. A failure
   means the bridge is not running; do not fabricate results.
2. Read before writing. For scenes use `GET /scene?depth=1` (raise `depth` only when needed; small
   scenes default to `depth=2`). Locate exact objects with `POST /object/find` (`{"name": "..."}`)
   and get details with `POST /object/inspect` (`{"object": {"id": 123}}`).
3. List assets with `GET /assets?type=Prefab` or `?type=Material` before referencing paths; never
   guess asset paths.
4. Prefer the typed endpoints. Fall back to `POST /code/execute` only when logic is beyond them
   (loops, batch generation, runtime APIs, custom SerializedObject work, unsupported field types).
5. Verify after each change by re-reading the affected object/scene. Confirm results in plain
   language.
6. After meaningful changes offer to save the scene: `POST /scene/save` (pass `path` only for a new
   scene).

## Key endpoints

| Call | JSON body (as needed) |
|---|---|
| `GET /health` | — |
| `GET /status` | — |
| `GET /project`, `GET /scenes` | — |
| `POST /scene/open` | `{"path": "Assets/Scenes/Main.unity"}` |
| `POST /scene/save` | `{"path": "Assets/Scenes/New.unity"}` (optional) |
| `GET /scene` | query `depth`, `full`, `noHierarchy` |
| `POST /object/create` | `{"name": ..., "type": "cube"/"empty", "parent": {"id": ...}, "localPosition": [x,y,z], "localScale": [x,y,z], "select": true}` |
| `POST /object/update` | `{"object": {"id":...}, "name": ..., "localPosition": [..], "localRotation": [..] (Euler), "localScale": [..], "active": true, "tag": ..., "layer": ...}` |
| `POST /object/delete` | `{"object": {"id": 123}}` |
| `POST /object/inspect` | `{"object": {...}, "includeFields": true}` |
| `POST /object/select` | `{"object": {...}}` |
| `POST /object/find` | `{"name": "Player", "limit": 50}` |
| `POST /component/add` | `{"object": {...}, "type": "Rigidbody"}` |
| `POST /component/remove` | `{"object": {...}, "type": "Rigidbody"}` |
| `POST /component/set` | `{"object": {...}, "type": "Rigidbody", "field": "m_Mass", "value": 12}` |
| `GET /assets` | query `type`, `name`, `limit` |
| `POST /prefab/instantiate` | `{"prefab": "Assets/Prefabs/Orb.prefab", "parent": {"id":...}, "localPosition": [..], "select": true}` |
| `POST /material/create` | `{"name": "Gold", "color": [1, 0.84, 0, 1]}` |
| `POST /material/set` | `{"material": "Assets/Materials/Gold.mat", "property": "_BaseColor", "type": "color", "value": [..]}` |
| `POST /code/execute` | `{"code": "var go = GPT.Find(\"Player\"); GPT.Result(go.transform.position);"}` |
| `POST /play` | `{"on": true}` / `{"on": false}` or `POST /pause` `{"on": true}` / `POST /stop` |

**Object locators**: `object` may be `{"id": <instanceId>}` (most reliable), `{"name": "..."}`, or
`{"path": "Parent/Child"}`. Transforms are **local** by default; use `worldPosition` for absolute
world position. Colors are `[r,g,b,a]` 0..1 floats or a `"#RRGGBB"` string.

## `POST /code/execute` (arbitrary C# in the Editor)

The `code` string becomes the body of a static method run on the Unity main thread. Helpers:

- `GPT.Log(object)` appends to `logs`; `GPT.Result(object)` sets the returned `result`.
- `GPT.Find(name)` / `GPT.FindAll(name)` search scene objects (incl. inactive).
- `GPT.Select(go)`, `GPT.Destroy(go)`, `GPT.SetDirty(obj)`.

Full `UnityEngine.*` / `UnityEditor.*` APIs are available. Compilation triggers a domain reload that
takes a few seconds; the call blocks until it finishes. Example:

```bash
curl -s -H "X-Auth-Token: $TOKEN" http://127.0.0.1:8765/code/execute -d '{
  "code": "for (int x = 0; x < 5; x++) for (int z = 0; z < 5; z++) { var s = GameObject.CreatePrimitive(PrimitiveType.Sphere); s.name = \"Orb_\" + x + \"_\" + z; s.transform.localPosition = new Vector3(x * 1.2f, 0, z * 1.2f); Undo.RegisterCreatedObjectUndo(s, \"Orb\"); } GPT.Result(\"Created 25 orbs\");",
  "timeoutSec": 180
}'
```

## Usage rules

- Only one Unity project is served: the one open in the Editor on this machine.
- Destructive actions (delete, overwrite, play mode) — proceed only when the user asked for them.
- Do not `POST /server/stop` unless the user asks.
- If a call fails, read `error`, fix the request, retry once or twice; if it still fails, explain the
  exact error to the user.
- `/code/execute` mutations are not undoable; say so when it matters. Typed endpoint mutations are.
- Never claim the project changed unless the API returned `ok: true`.