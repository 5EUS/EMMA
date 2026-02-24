# EMMA CLI

Simple CLI that calls the embedded API adapter backed by the plugin host pipeline.

## Usage

```bash
EMMA_PLUGIN_HOST_URL=http://localhost:5001 EMMA_PLUGIN_ID=demo dotnet run --project src/EMMA.Cli demo
```

Arguments:
- First argument is the search query (defaults to `demo`).

Environment:
- `EMMA_PLUGIN_HOST_URL` (default: `http://localhost:5001`)
- `EMMA_PLUGIN_ID` (default: `demo`)