# plugins/ — runtime-loaded third-party assemblies

Drop operator-approved third-party DLLs here. They are loaded **dynamically at runtime**
(not built into the host) and become referenceable from helper components only when also
listed in `GrantableAssemblies`.

- **Local run:** `./setup-plugins.sh` copies `Newtonsoft.Json.dll` into this folder; the build
  copies it next to the binary. Then `dotnet run` lights up step [6].
- **Docker:** this folder is replaced by a mounted volume — see `../docker-compose.yml`:
  ```yaml
  volumes:
    - ./plugins:/app/assemblies:ro
  environment:
    - SCRIPT_PLUGIN_DIR=/app/assemblies
  ```

`.dll` files here are intentionally git-ignored — supply them via the volume, not the repo.
