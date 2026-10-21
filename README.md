# Photo Map Viewer

A slideshow for a folder of photos (subfolders included). The photo fills the left side; the right side shows a map of where it was taken.

## Download

Open the **Actions** tab, click the newest green run, and download **PhotoMapViewer** under *Artifacts*. Unzip it anywhere and run `PhotoMapViewer.exe`.

The first time, Windows may say *"Windows protected your PC"*: click **More info → Run anyway**. That happens because the app isn't signed with a paid certificate.

## Using it

| Key | Action |
|---|---|
| → / ← | Next / previous photo |
| Space | Pause / play |
| R | Change order (date, name, random in folder, random everywhere) |
| M | Show / hide the map |
| S | Settings |
| O | Open another folder |
| F11 or F | Full screen (Esc to leave) |
| L | Set the location of this photo |
| A | Add locations, folder by folder |
| Ctrl+Z | Undo the last location change |

- **Zoom (auto):** a photo in the Netherlands shows the Netherlands, elsewhere in Europe shows Europe, anywhere else shows the world. Settings offer *always World / Europe / Netherlands* or a custom zoom that follows the photo.
- **Trail:** earlier photos stay on the map as fading dots; in random order only the last few jumps are drawn.
- **Photos without GPS:** the location is borrowed from a photo taken within a couple of hours (same folder preferred). Guessed spots get a dashed marker and ≈ in the caption.
- **Your own locations:** press **L** (or the pin button) and drag the blue pin, click the map, or search for a place. **A** walks through every folder that has photos without a location, one photo per folder, and suggests a spot from the folder or file name ("Kreta 2012" → Crete). Folders with several places can be done one day at a time. Your locations show with a blue marker and always win over the file's own GPS and over guesses.
- **Where they're kept:** in a hidden file `PhotoMap locations.json` in the opened folder, or in a folder above it that already has one, so it travels with your photos. A backup copy is kept in the app's own folder; if the file ever goes missing, the app offers to restore it. Your photo files are not changed.
- **Write into the photos:** the pin menu → *Write my locations into the photo files* (this photo / this folder / all). Uses the bundled ExifTool, keeps the files' dates, asks before doing anything.
- **HEIC** (iPhone) photos work without installing anything.
- The app remembers the last folder and photo, and keeps a scan index so reopening a large collection is instant.

Settings, the scan index and the place-name cache live in `%LOCALAPPDATA%\PhotoMapViewer`.

## Not yet

- Video playback (with length limit and speed): next version.

## Building

Every push to `main` is built by GitHub Actions on Windows (`.github/workflows/build.yml`). Locally: `dotnet publish src/PhotoMapViewer -c Release -o publish` on Windows with the .NET 8 SDK.

Map data and place search © OpenStreetMap contributors (Nominatim). ExifTool by Phil Harvey. Country borders: Natural Earth (public domain), via world-atlas. Map library: Leaflet (BSD-2).
