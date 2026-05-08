# StreamToCsv

Subscribes to AudD's longpoll for one stream and appends every recognized
song to a CSV file. Two modes:

- **Provision-and-listen** — `--url URL` adds the stream (with `--radio-id`,
  defaulting to `99999`), listens, and deletes the stream on exit.
- **Listen-only** — `--radio-id N` against an existing slot. Doesn't add or
  delete anything.

## Usage

```bash
export AUDD_API_TOKEN=...

# provision a stream, listen, delete on Ctrl-C
dotnet run --project examples/StreamToCsv -- \
  --url https://npr-ice.streamguys1.com/live.mp3

# explicit slot, custom output path
dotnet run --project examples/StreamToCsv -- \
  --url https://npr-ice.streamguys1.com/live.mp3 \
  --radio-id 123 \
  --output radio_log.csv

# listen-only against an existing slot
dotnet run --project examples/StreamToCsv -- --radio-id 123
```

Default output: `audd_stream_tracks.csv` (append mode, flushed per row).

## Callback URL

Longpoll requires a callback URL to be configured server-side, even if you
don't run an HTTP receiver — any URL that returns 200 OK works.

- In provision mode, if your account has no callback URL set the example
  configures `https://audd.tech/empty/` for you and tells you on exit.
- In listen-only mode, the example refuses to start when no callback URL
  is configured: that's a deliberate setup step, not something to paper over.

## CSV columns

`received_at,radio_id,timestamp,score,artist,title,album,song_link`

`received_at` is the wall-clock UTC time the row was written;
`timestamp` is the server-side play timestamp from the callback payload.

Notification envelopes (codes `0` / `650` / `651`) are written to stderr,
not the CSV.
