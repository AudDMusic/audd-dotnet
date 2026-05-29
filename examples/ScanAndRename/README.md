# ScanAndRename

Walks a folder, sends every audio file to AudD, writes the matched
artist/title/album back into the file's tags, and renames the file to
`Artist - Title.ext`. Default mode is dry-run.

## Usage

```bash
export AUDD_API_TOKEN=...

# preview only — no files are touched
dotnet run --project examples/ScanAndRename -- /path/to/music

# actually mutate tags + rename, with 8 parallel recognitions
dotnet run --project examples/ScanAndRename -- /path/to/music --apply --concurrency 8
```

Recognized extensions: `.mp3 .flac .ogg .opus .m4a .mp4 .wav .aac`.

`--apply` writes tags via [TagLib#](https://github.com/mono/taglib-sharp)
(LGPL-2.1) and renames in place. There is no built-in undo — keep a backup
or run on a copy. Files whose target name already exists are skipped to
avoid clobbering.
