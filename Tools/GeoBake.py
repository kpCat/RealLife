"""Build RealLife's local preview and walkable Stăuceni package from offline source files."""

import argparse
import hashlib
import json
import math
import re
import xml.etree.ElementTree as ET
from pathlib import Path

import rasterio
from PIL import Image, ImageDraw


LAT, LON = 47.0935, 28.868
AXIS = 6378137.0
ECC2 = (1 / 298.257223563) * (2 - 1 / 298.257223563)
LEVEL, FACE, SIZE = 16, 4, 1 << 16
STEPS = 16
WIDTHS = {
    "trunk": 8, "primary": 7.5, "secondary": 7, "tertiary": 6.2,
    "residential": 5.2, "unclassified": 5, "living_street": 4.3, "service": 3.2,
    "track": 2.6, "path": 1.25, "footway": 1.6, "pedestrian": 3, "cycleway": 2,
}
TRAILS = {"path", "footway", "pedestrian", "cycleway", "track"}


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def ecef(lat, lon, h=0):
    lat, lon = math.radians(lat), math.radians(lon)
    s = math.sin(lat)
    n = AXIS / math.sqrt(1 - ECC2 * s * s)
    return ((n + h) * math.cos(lat) * math.cos(lon),
            (n + h) * math.cos(lat) * math.sin(lon),
            (n * (1 - ECC2) + h) * s)


def geodetic(p):
    scale = AXIS / math.sqrt(p[0] ** 2 + p[1] ** 2 + p[2] ** 2 / (1 - ECC2))
    x, y, z = (coordinate * scale for coordinate in p)
    horizontal = math.hypot(x, y)
    lat = math.atan2(z, horizontal * (1 - ECC2))
    for _ in range(6):
        s = math.sin(lat)
        n = AXIS / math.sqrt(1 - ECC2 * s * s)
        lat = math.atan2(z + ECC2 * n * s, horizontal)
    return math.degrees(lat), math.degrees(math.atan2(y, x))


def grid(p):
    x, y, z = p
    if z <= max(abs(x), abs(y)):
        raise ValueError("The Stăuceni baker expects the +Z face")
    return ((x / z + 1) * SIZE / 2, (y / z + 1) * SIZE / 2)


def direction(x, y):
    return (2 * x / SIZE - 1, 2 * y / SIZE - 1, 1)


def clip(a, b, rect):
    """Clip a segment to the closed coverage in cube-face coordinates."""
    t0, t1 = 0, 1
    for axis in range(2):
        delta = b[axis] - a[axis]
        lo, hi = rect[axis], rect[axis + 2]
        if abs(delta) < 1e-12:
            if not lo <= a[axis] <= hi:
                return None
            continue
        a0, a1 = sorted(((lo - a[axis]) / delta, (hi - a[axis]) / delta))
        t0, t1 = max(t0, a0), min(t1, a1)
    if t1 - t0 < 1e-9:
        return None
    return tuple(tuple(a[i] + (b[i] - a[i]) * t for i in range(2)) for t in (t0, t1))


def split(a, b):
    cuts = {0.0, 1.0}
    for axis in range(2):
        lo, hi = sorted((a[axis], b[axis]))
        for line in range(math.floor(lo) + 1, math.ceil(hi)):
            cuts.add((line - a[axis]) / (b[axis] - a[axis]))
    samples = [tuple(a[i] + (b[i] - a[i]) * t for i in range(2)) for t in sorted(cuts)]
    return zip(samples, samples[1:])


def sampler(path, bounds):
    from rasterio.windows import Window, from_bounds
    with rasterio.open(path) as image:
        if image.crs.to_epsg() not in (4326, 4979):
            raise ValueError(f"{path} must use a WGS84 horizontal CRS")
        region = from_bounds(*bounds, transform=image.transform)
        window = Window(math.floor(region.col_off) - 1, math.floor(region.row_off) - 1,
                        math.ceil(region.width) + 4, math.ceil(region.height) + 4)
        data = image.read(1, window=window, masked=True)
        transform = ~image.window_transform(window)

    def sample(lat, lon):
        x, y = transform * (lon, lat)
        x, y = x - .5, y - .5
        i, j = math.floor(x), math.floor(y)
        if not (0 <= i < data.shape[1] - 1 and 0 <= j < data.shape[0] - 1):
            raise ValueError(f"Missing raster coverage in {path}")
        xf, yf = x - i, y - j
        values = data[j:j + 2, i:i + 2]
        if values.mask.any() or not all(math.isfinite(float(v)) for v in values.flat):
            raise ValueError(f"Missing raster samples in {path}")
        return float((values[0, 0] * (1 - xf) + values[0, 1] * xf) * (1 - yf) +
                     (values[1, 0] * (1 - xf) + values[1, 1] * xf) * yf)

    return sample


def polygon_area(points):
    return sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(points, points[1:] + points[:1])) / 2


def cross(a, b, c):
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def triangulate(points):
    """Ear clipping for an OSM outer ring, including concave footprints."""
    ring = list(range(len(points)))
    if polygon_area(points) < 0:
        ring.reverse()
    triangles = []
    while len(ring) > 3:
        for n, b in enumerate(ring):
            a, c = ring[n - 1], ring[(n + 1) % len(ring)]
            s, t, u = points[a], points[b], points[c]
            if cross(s, t, u) <= 1e-5:
                continue
            if any(min(cross(s, t, points[i]), cross(t, u, points[i]), cross(u, s, points[i])) >= -1e-6
                   for i in ring if i not in (a, b, c)):
                continue
            triangles.extend((a, c, b))  # Local X/Z becomes Unity's upward-facing Y.
            ring.pop(n)
            break
        else:
            return None
    return triangles + [ring[0], ring[2], ring[1]]


def meters(value, default, lo, hi):
    match = re.fullmatch(r"\s*(\d+(?:[.,]\d+)?)\s*(?:m)?\s*", value or "")
    return min(hi, max(lo, float(match[1].replace(",", ".")))) if match else default


def point(xz):
    return {"X": round(xz[0], 4), "Z": round(xz[1], 4)}


def write_json(path, value):
    pending = path.with_suffix(".tmp")
    pending.write_bytes(canonical_json(value))
    pending.replace(path)


def canonical_json(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def bake_region(args, output, sources):
    x, y = map(math.floor, grid(ecef(LAT, LON)))
    rect = (x - 7, y - 6, x + 7, y + 7)
    corners = [geodetic(direction(x, y)) for x in (rect[0], rect[2]) for y in (rect[1], rect[3])]
    min_lat, max_lat = min(p[0] for p in corners), max(p[0] for p in corners)
    min_lon, max_lon = min(p[1] for p in corners), max(p[1] for p in corners)
    bounds = (min_lon, min_lat, max_lon, max_lat)
    dem, geoid = sampler(args.dem, bounds), sampler(args.geoid, bounds)
    height = dem(LAT, LON) + geoid(LAT, LON)
    o = ecef(LAT, LON, height)
    la, lo = math.radians(LAT), math.radians(LON)
    east = (-math.sin(lo), math.cos(lo), 0)
    north = (-math.sin(la) * math.cos(lo), -math.sin(la) * math.sin(lo), math.cos(la))
    up = (math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la))

    def flat(xy):
        ray = direction(*xy)
        scale = dot(o, up) / dot(ray, up)
        offset = tuple(r * scale - a for r, a in zip(ray, o))
        return dot(offset, east), dot(offset, north)

    def elevation(x, y):
        lat, lon = geodetic(direction(x / STEPS, y / STEPS))
        return round(dot(tuple(p - a for p, a in zip(ecef(lat, lon, dem(lat, lon) + geoid(lat, lon)), o)), up), 3)

    samples = {(x, y): elevation(x, y)
               for y in range(rect[1] * STEPS, rect[3] * STEPS + 1)
               for x in range(rect[0] * STEPS, rect[2] * STEPS + 1)}
    local_corners = [flat((rect[i], rect[j])) for i, j in ((0, 1), (2, 1), (2, 3), (0, 3))]
    chunks = {(x, y): {"X": x, "Y": y, "Roads": [], "Buildings": [],
                        "Heights": [samples[x * STEPS + i, y * STEPS + j]
                                    for j in range(STEPS + 1) for i in range(STEPS + 1)]}
              for y in range(rect[1], rect[3]) for x in range(rect[0], rect[2])}

    osm = ET.parse(args.osm).getroot()
    coverage = osm.find("bounds")
    if coverage is None or not (float(coverage.get("minlat")) <= min_lat < max_lat <= float(coverage.get("maxlat"))
                               and float(coverage.get("minlon")) <= min_lon < max_lon <= float(coverage.get("maxlon"))):
        raise ValueError("OSM snapshot does not cover the requested cells")
    nodes = {n.get("id"): grid(ecef(float(n.get("lat")), float(n.get("lon")))) for n in osm.iter("node")}
    roads, buildings = set(), set()
    routes = {}
    for way in sorted(osm.iter("way"), key=lambda w: int(w.attrib["id"])):
        tags = {t.attrib["k"]: t.attrib["v"] for t in way.iter("tag")}
        refs = [n.attrib["ref"] for n in way.iter("nd")]
        if len(refs) < 2 or any(ref not in nodes for ref in refs):
            continue
        coords = [nodes[ref] for ref in refs]
        id_ = int(way.attrib["id"])
        road = tags.get("highway")
        if road in WIDTHS and tags.get("area") != "yes" and "tunnel" not in tags and "bridge" not in tags:
            width = meters(tags.get("width"), WIDTHS[road], 0.8, 16)
            sidewalk = tags.get("sidewalk", "")
            side = {"both": 3, "yes": 3, "left": 1, "right": 2}.get(sidewalk, 0)
            route = []
            for index, (a, b) in enumerate(zip(coords, coords[1:])):
                part = clip(a, b, rect)
                if part is None:
                    continue
                if not route:
                    route.append(point(flat(part[0])))
                route.append(point(flat(part[1])))
                for segment, (p, q) in enumerate(split(*part)):
                    mid = tuple(math.floor((a + b) / 2) for a, b in zip(p, q))
                    if mid not in chunks or math.dist(p, q) < 1e-5:
                        continue
                    item = {"Id": f"osm:way:{id_}", "Part": index * 64 + segment,
                            "A": point(flat(p)), "B": point(flat(q)), "Width": width,
                            "Trail": road in TRAILS or tags.get("surface") in ("ground", "gravel", "dirt", "unpaved"),
                            "Sidewalk": side}
                    chunks[mid]["Roads"].append(item)
                    roads.add(id_)
            if len(route) > 1:
                routes[id_] = route

        if tags.get("building") in (None, "no") or refs[0] != refs[-1]:
            continue
        coords = coords[:-1]
        if len(coords) < 3 or not all(rect[0] < u < rect[2] and rect[1] < v < rect[3] for u, v in coords):
            continue
        footprint = list(dict.fromkeys(tuple(round(x, 4) for x in flat(p)) for p in coords))
        if len(footprint) < 3 or abs(polygon_area(footprint)) < 5:
            continue
        tris = triangulate(footprint)
        if tris is None:
            continue
        owner = tuple(math.floor(sum(p[i] for p in coords) / len(coords)) for i in range(2))
        fallback = 3.1 + 0.65 * (id_ % 4)
        levels = meters(tags.get("building:levels"), 0, 1, 18)
        h = meters(tags.get("height"), (2.9 * levels + .5) if levels else fallback, 2.4, 60)
        chunks[owner]["Buildings"].append({"Id": f"osm:way:{id_}", "Footprint": [point(p) for p in footprint],
                                           "Triangles": tris, "Height": h, "Style": id_ % 4})
        buildings.add(id_)

    # A continuous public street through the region is a real controller smoke route.
    route = routes.get(106509156)
    if route is None or sum(math.hypot(a["X"] - b["X"], a["Z"] - b["Z"]) for a, b in zip(route, route[1:])) < 450:
        raise ValueError("The public street used by the smoke route is missing or too short")
    route = route[1:-1]
    if len(route) < 2 or len(roads) < 10 or len(buildings) < 100:
        raise ValueError("Incomplete regional geometry")
    snapshot = max((element.get("timestamp", "") for element in osm), default="")
    result = {"Format": 1, "GridVersion": 1, "PackageId": "md.stauceni",
              "Title": "Стэучены", "Latitude": LAT, "Longitude": LON, "Height": height,
              "Face": FACE, "Level": LEVEL, "MinX": rect[0], "MinY": rect[1], "MaxX": rect[2], "MaxY": rect[3],
              "South": min_lat, "West": min_lon, "North": max_lat, "East": max_lon,
              "Steps": STEPS, "Outline": [point(p) for p in local_corners],
              "Route": route, "Chunks": list(chunks.values()),
              "Sources": [{"Name": name, "Sha256": sha} for name, sha in sources.items()],
              "OsmLatestEdit": snapshot, "Coordinates": "WGS84 ECEF / ENU; GLO-30 EGM2008 + NGA geoid",
              "Attribution": "© OpenStreetMap contributors • Copernicus DEM • Natural Earth"}
    revision = hashlib.sha256(canonical_json(result)).hexdigest()[:20]
    result["Revision"] = revision
    write_json(output / "region.json", result)
    print(f"{len(chunks)} cells, {len(roads)} OSM roads, {len(buildings)} OSM buildings, bounds {bounds}, revision {revision}")


def bake_globe(input_path, output):
    width, height = 2048, 1024
    ocean, land, border = (25, 57, 75), (92, 126, 109), (163, 176, 139)
    image = Image.new("RGB", (width, height), ocean)
    draw = ImageDraw.Draw(image)
    for lon in range(-180, 180, 15):
        x = round((lon + 180) * width / 360)
        draw.line((x, 0, x, height), fill=(35, 69, 85))
    for lat in range(-75, 90, 15):
        y = round((90 - lat) * height / 180)
        draw.line((0, y, width, y), fill=(35, 69, 85))
    data = json.loads(input_path.read_text(encoding="utf-8"))
    for feature in data["features"]:
        geometry = feature["geometry"]
        polygons = geometry["coordinates"] if geometry["type"] == "MultiPolygon" else [geometry["coordinates"]]
        tone = sum(map(ord, feature["properties"]["ADMIN"])) % 16 - 8
        color = tuple(c + tone for c in land)
        for polygon in polygons:
            for i, ring in enumerate(polygon):
                px = [((lon + 180) * width / 360, (90 - lat) * height / 180) for lon, lat in ring]
                draw.polygon(px, fill=color if i == 0 else ocean, outline=border)
    pending = output.with_suffix(".tmp")
    image.save(pending, format="PNG", optimize=True)
    pending.replace(output)


def crop_geoid(output):
    from rasterio.windows import Window
    with rasterio.open("https://cdn.proj.org/us_nga_egm08_25.tif") as source:
        row, column = source.index(28.868, 47.089)
        window = Window(column - 2, row - 2, 5, 5)
        with rasterio.open(output, "w", driver="GTiff", height=5, width=5, count=1,
                           dtype=source.dtypes[0], crs=source.crs, transform=source.window_transform(window)) as cropped:
            cropped.write(source.read(1, window=window), 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--crop-geoid", type=Path, help="Fetch the small EGM2008 grid window used by Stăuceni")
    for name in ("osm", "dem", "geoid", "earth", "out"):
        parser.add_argument(f"--{name}", type=Path)
    args = parser.parse_args()
    if args.crop_geoid:
        crop_geoid(args.crop_geoid)
        return
    if not all((args.osm, args.dem, args.geoid, args.earth, args.out)):
        parser.error("baking requires --osm, --dem, --geoid, --earth and --out")
    sources = {"OpenStreetMap": digest(args.osm), "Copernicus GLO-30": digest(args.dem),
               "NGA EGM2008 crop": digest(args.geoid), "Natural Earth": digest(args.earth)}
    args.out.mkdir(parents=True, exist_ok=True)
    bake_region(args, args.out, sources)
    bake_globe(args.earth, args.out / "earth.png")
    print("Sources:", json.dumps(sources))


if __name__ == "__main__":
    main()
