# Makes a few small sample photos (JPEG, PNG, HEIC) with GPS and dates, for the core checks.
import sys, os, piexif
from PIL import Image
import pillow_heif
pillow_heif.register_heif_opener()
out = sys.argv[1]
def dms(v):
    v = abs(v); d = int(v); m = int((v-d)*60); s = round(((v-d)*60-m)*60*1000)
    return ((d,1),(m,1),(s,1000))
def exif(dt, lat=None, lon=None, orient=1):
    e = {"0th": {piexif.ImageIFD.Orientation: orient}, "Exif": {piexif.ExifIFD.DateTimeOriginal: dt.encode()}, "GPS": {}}
    if lat is not None:
        e["GPS"] = {1: b'N' if lat>=0 else b'S', 2: dms(lat), 3: b'E' if lon>=0 else b'W', 4: dms(lon)}
    return piexif.dump(e)
os.makedirs(f"{out}/2024 Friesland", exist_ok=True)
os.makedirs(f"{out}/2007 Romania", exist_ok=True)
os.makedirs(f"{out}/Trips", exist_ok=True)
img = Image.new("RGB", (64, 48), (200, 80, 40))
img.save(f"{out}/2024 Friesland/sneek.jpg", exif=exif("2024:08:14 10:00:00", 53.0329, 5.6589, 6))
img.save(f"{out}/2024 Friesland/nogps_same_day.jpg", exif=exif("2024:08:14 11:30:00"))
img.save(f"{out}/2024 Friesland/nogps_next_day.jpg", exif=exif("2024:08:15 18:00:00"))
img.save(f"{out}/2007 Romania/breasta.jpg", exif=exif("2007:07:02 15:12:09", 44.3, 23.68))
img.save(f"{out}/Trips/nyc.png", exif=exif("2019:05:01 12:00:00", 40.7128, -74.006))
img.save(f"{out}/Trips/reykjavik.heic", exif=exif("2021:03:03 09:00:00", 64.1466, -21.9426))
img.save(f"{out}/Trips/plain.jpg")
open(f"{out}/Trips/clip.mp4","wb").write(b"\0\0\0\x18ftypmp42")
print("done")
