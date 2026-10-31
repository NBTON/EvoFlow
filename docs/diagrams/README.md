# README diagrams

The root README displays static SVGs rendered with [Pele](https://github.com/obsidianmd/pele)
1.13.0. Editable Mermaid sources live here; generated images live in `docs/images`.
The renderer embeds EvoFlow's dark/green palette so GitHub needs no JavaScript,
external fonts or rendering service.

From this directory, with Node.js 20 or later:

```powershell
npm ci --ignore-scripts
npm run render
```

Commit the `.mmd` sources and regenerated SVGs together. This tooling is only
for documentation; it is not a dependency of the native application or worker.
