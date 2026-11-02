"""Export shop artwork from an existing local client extraction.

Requires Python 3.12 and Pillow in the active Python environment.
No client, game data, save, or extracted source is changed. The manifest retains
source hashes and exact atlas rectangles; absent art is reported, never invented.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import mmap
from pathlib import Path
import re
import sqlite3
import xml.etree.ElementTree as ET

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True,
                        help="Existing extraction root containing extracted/StreamingAssets/GameData and atlas metadata")
    parser.add_argument("--content", type=Path, default=Path(__file__).resolve().parents[1] / "content")
    parser.add_argument("--native-data", type=Path, required=True,
                        help="Current client's data folder; verify every configured native banner resource")
    parser.add_argument("--offer", action="append", default=[],
                        help="Incrementally export only this configured offer ID (repeatable); retain all other artwork and names")
    args = parser.parse_args()
    corpus, content = args.corpus.resolve(), args.content.resolve()
    output = content / "shop-art"
    output.mkdir(parents=True, exist_ok=True)
    data = corpus / "extracted/StreamingAssets/GameData"
    sources, entries, missing = {}, {}, {}
    images = {}
    name_tags = {}
    # Exact cosmetic-only banners; the misspelling in amerpaw_top is the original resource key.
    # Keep each existing web ItemAtlas icon and map native artwork independently.
    individual_banners = {
        "SKIN_COUGAR_SHADOWCAT_ORANGE": ("illu_amerpaw_top", "illu_amberpaw_bottom"),
        "SKIN_HORSE_MONK_FIRE": ("illu_ember_mare_top", "illu_ember_mare_bottom"),
        "SKIN_COUGAR_JADE_GUARDIAN": ("illu_jade_guardian_top", "illu_jade_guardian_bottom"),
        "SKIN_DIREBEAR_PANDA_TELLAFRIEND": ("illu_panda_top", "illu_panda_bottom"),
    }
    manifest_path = content / "shop-art.json"
    previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
    selected_offers = set(args.offer)
    if selected_offers:
        if not previous:
            raise ValueError("Incremental offer export requires an existing artwork manifest")
        sources = dict(previous.get("sources", {}))
        entries = dict(previous.get("entries", {}))
        missing = dict(previous.get("missing", {}))
        name_tags = dict(previous.get("localizedNames", {}).get("tags", {}))

    def source(path: Path) -> str:
        key = path.relative_to(corpus).as_posix()
        if key not in sources:
            sources[key] = hashlib.sha256(path.read_bytes()).hexdigest()
        return key

    def read_json(path: Path):
        source(path)
        return json.loads(path.read_text(encoding="utf-8"))

    def read_xml(name: str):
        path = data / (name + ".xml")
        source(path)
        return ET.parse(path).getroot()

    def load_image(path: Path):
        source(path)
        if path not in images:
            images[path] = Image.open(path).convert("RGBA")
        return images[path]

    def save(key: str, image, origin, native=""):
        leaf = re.sub(r"[^a-z0-9_.-]", "_", key.lower()) + ".png"
        path = output / leaf
        prior = previous.get("entries", {}).get(key)
        input_files = ([origin["file"]] if "file" in origin else origin.get("files", []))
        if prior and prior.get("source") == origin and path.exists() \
                and all(previous.get("sources", {}).get(f) == sources.get(f) for f in input_files) \
                and hashlib.sha256(path.read_bytes()).hexdigest() == prior.get("sha256"):
            entries[key] = {**prior, "native": native}
            return
        image.save(path, format="PNG", optimize=True)
        entries[key] = {"native": native, "file": leaf, "source": origin,
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "width": image.width, "height": image.height}

    sprites = {s["name"]: s for s in read_json(corpus / "client/assets/ui/sprites.json")
               if "ItemAtlas" in s["texture"]}
    items = {e.get("uniquename"): (e.tag, dict(e.attrib))
             for e in read_xml("items") if e.get("uniquename")}
    inventory_icons = None

    def priced(element):
        return element.get("state", "Buyable" if int(element.get("goldprice", "0")) else "Unlocked") != "Unlocked"

    desired = {}
    for e in read_xml("mountskins").findall("./mountcategory/mountskin"):
        name = e.get("reference", "")
        if priced(e) and items.get(name, (None,))[0] == "mountskin":
            desired[name] = "mount"
    seen = set()
    for e in read_xml("wardrobe").findall("./wardrobecategory/item"):
        name = e.get("reference", "")
        if name in seen:
            continue
        seen.add(name)
        if priced(e) and items.get(name, (None,))[0] in ("equipmentitem", "weapon"):
            desired[name] = "skin"

    for name, kind in desired.items():
        if selected_offers:
            continue
        name_tags[name] = items[name][1].get("namelocatag", "@ITEMS_" + name)
        sprite_name = items[name][1].get("uisprite", name)
        sprite = sprites.get(sprite_name)
        # Canonical inventory resolution removes tier prefixes for shared cape
        # art. Use its recorded icon, never a nearest-name or family substitute.
        if sprite is None:
            if inventory_icons is None:
                inventory_icons = {key: value.get("icon") for key, value in
                                   read_json(corpus / "client/assets/inventory/catalog.json")["items"].items()}
            recorded = inventory_icons.get(name)
            if recorded in sprites:
                sprite_name, sprite = recorded, sprites[recorded]
        if sprite is None:
            missing[name] = {"kind": kind, "reason": "No exact ItemAtlas sprite in existing extraction", "sprite": sprite_name}
            continue
        path = corpus / "client" / sprite["texture"].removeprefix("res://")
        atlas, rect = load_image(path), sprite["source"]
        x, y, w, h = (int(rect[k]) for k in ("x", "y", "width", "height"))
        if x < 0 or y < 0 or x + w > atlas.width or y + h > atlas.height:
            raise ValueError(f"Atlas bounds: {name}")
        crop = atlas.crop((x, y, x + w, y + h))
        # NGUI sprite padding restores the original icon canvas without rescaling.
        left, right, top, bottom = (int(rect.get("padding" + k, 0)) for k in ("Left", "Right", "Top", "Bottom"))
        icon = Image.new("RGBA", (w + left + right, h + top + bottom), (0, 0, 0, 0))
        icon.paste(crop, (left, top))
        save(name, icon, {"file": source(path), "sprite": sprite_name, "rect": [x, y, w, h],
                          "padding": [left, top, right, bottom]}, individual_banners.get(name, ("", ""))[0])

    portraits = read_json(corpus / "client/assets/vanity/portraits.json")
    avatars = read_xml("avatars")
    for group, tag, kind in (("peraccountavatars", "avatar", 4), ("peraccountavatarrings", "avatarring", 5)):
        for element in avatars.findall(f"./{group}/{tag}"):
            if not priced(element):
                continue
            name = element.get("name", "")
            # Portraits and frames are real account unlocks too, including in starter bundles.
            desired[name] = tag
            if selected_offers:
                continue
            name_tags[name] = "@AVATAR_NAME_" + name
            entry = portraits["entries"].get(f"{kind}:{name}", {})
            aliases = list(dict.fromkeys(entry.get("texture_aliases", {}).values()))
            paths = [corpus / "client" / portraits["textures"][alias].removeprefix("res://")
                     for alias in aliases if alias in portraits["textures"]]
            if not paths or len(paths) != len(aliases):
                missing[name] = {"kind": tag, "reason": "Exact portrait texture absent from canonical extraction"}
                continue
            parts = [load_image(path) for path in paths]
            # Where the original has male/female portraits, show both real variants.
            canvas = Image.new("RGBA", (sum(im.width for im in parts), max(im.height for im in parts)), (0, 0, 0, 0))
            x = 0
            for part in parts:
                canvas.paste(part, (x, (canvas.height - part.height) // 2))
                x += part.width
            save(name, canvas, {"files": [source(path) for path in paths], "resources": aliases})

    localization_path = data / "localization.xml"
    names_path = content / "shop-names.es.json"
    if selected_offers:
        translated_names = json.loads(names_path.read_text(encoding="utf-8"))
    else:
        characters = read_xml("characters")
        for element in list(characters.findall("./Customizations/Customization/Item/Mesh")) + list(characters.findall("./Customizations/ColorSet/Color")):
            if priced(element):
                name = element.get("uniquename", "")
                name_tags[name] = element.get("namelocatag", "")
                missing[name] = {"kind": "customization", "reason": "3D customization; no exact static shop icon in the inspected indexes"}

        # Read only the requested localization entries from the original UTF-8 XML.
        # The older prepared JSON replaced some accented characters with U+FFFD.
        source(localization_path)
        requested_tags = set(name_tags.values()) - {""}
        localized = {}
        language_attribute = "{http://www.w3.org/XML/1998/namespace}lang"
        for _, element in ET.iterparse(localization_path, events=("end",)):
            if element.tag != "tu":
                continue
            tag = element.get("tuid", "")
            if tag in requested_tags:
                for language in element.findall("tuv"):
                    if language.get(language_attribute) != "ES-ES":
                        continue
                    segment = language.find("seg")
                    value = "" if segment is None else "".join(segment.itertext()).strip()
                    if value and "\ufffd" not in value:
                        localized[tag] = value
            element.clear()
        translated_names = {name: localized[tag] for name, tag in sorted(name_tags.items()) if tag in localized}
        names_staging = names_path.with_suffix(".json.tmp")
        names_staging.write_text(json.dumps(translated_names, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        names_staging.replace(names_path)

    # Lookup only the named offer textures. ResourceManager is a large one-line
    # file, so mmap extracts the small resource index without decoding its tree.
    resource_index = corpus / "reports/ResourceManager.json"
    pattern = rb'\["(gui/mainatlasicons/[^"\\]+)", \{"m_FileID": (\d+), "m_PathID": (\d+)\}\]'
    with resource_index.open("rb") as handle, mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
        resources = {m.group(1).decode().split("/")[-1]: int(m.group(3)) for m in re.finditer(pattern, mapped)}
    database = sqlite3.connect("file:" + (corpus / "reports/catalog.sqlite").as_posix() + "?mode=ro&immutable=1", uri=True)

    def offer(key, name):
        if selected_offers:
            return
        path_id = resources[name]
        row = database.execute("SELECT name,output FROM assets WHERE source=? AND id=?", ("resources.assets", path_id)).fetchone()
        if row is None or row[0] != name:
            raise ValueError(f"Resource/texture mismatch: {name}")
        path = corpus / row[1]
        save(key, load_image(path), {"file": source(path), "resource": "gui/mainatlasicons/" + name,
                                    "asset": "resources.assets", "pathId": path_id}, name)

    offer("category:gold", "illu_offer_gold_01")
    offer("category:premium", "illu_offer_premium_01")
    for amount, index in ((1000, 1), (5000, 3), (15000, 5)):
        offer(f"product:gold.{amount}", f"illu_offer_gold_{index:02d}")
    for days, index in ((7, 0), (30, 1), (90, 2), (180, 3), (360, 4)):
        for currency in ("cash", "gold", "silver"):
            offer(f"product:premium.{days}.{currency}", f"illu_offer_premium_{index:02d}")

    # Authentic promotional art for this exact collection; individual components
    # above remain available to display the pack contents independently.
    row = database.execute("SELECT name,output FROM assets WHERE source=? AND id=?", ("resources.assets", 16032)).fetchone()
    if not selected_offers and row and row[0] == "ad_duelist":
        path = corpus / row[1]
        save("product:bundle.duelist", load_image(path), {"file": source(path), "asset": "resources.assets", "pathId": 16032})
    # Curated offers explicitly select original MainAtlasIcons illustrations.
    # NGUI LargeImageTile uses top in dw8 and bottom in dxc. The browser receives the
    # two original halves joined without scaling, redrawing, or substitution.
    catalog_path = content / "shop.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    unknown_offers = selected_offers - {entry["Id"] for entry in catalog.get("Offers", [])}
    if unknown_offers:
        raise ValueError(f"Unknown configured offers: {sorted(unknown_offers)}")
    native_data = args.native_data.resolve()
    native_manager = native_data / "globalgamemanagers"
    with native_manager.open("rb") as file, mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
        native_resources = {value.decode("ascii") for value in
                            re.findall(rb"gui/mainatlasicons/illu_[a-z0-9_]+", mapped)}
        native_manager_hash = hashlib.sha256(mapped).hexdigest()
    configured_resources = {"gui/mainatlasicons/" + offer["Artwork"][part]
                            for offer in catalog.get("Offers", []) if offer.get("Artwork")
                            for part in ("Native", "Bottom") if offer["Artwork"].get(part)}
    configured_resources.update("gui/mainatlasicons/" + name
                                for pair in individual_banners.values() for name in pair)
    unavailable = configured_resources - native_resources
    if unavailable:
        raise ValueError(f"Banner resources absent from current client: {sorted(unavailable)}")
    native_reference = {"version": (native_data.parent / "version.txt").read_text(encoding="utf-8").strip(),
                        "resourceManager": native_manager.as_posix(), "sha256": native_manager_hash,
                        "verifiedBannerResources": len(configured_resources)}
    for unlock, names in individual_banners.items():
        if selected_offers:
            continue
        if unlock not in desired or unlock not in entries:
            raise ValueError(f"Native banner has no exact original unlock/icon: {unlock}")
        paths, path_ids = [], []
        for name in names:
            if name not in resources:
                raise ValueError(f"Individual banner is outside MainAtlasIcons: {name}")
            path_id = resources[name]
            row = database.execute("SELECT name,output FROM assets WHERE source=? AND id=?", ("resources.assets", path_id)).fetchone()
            if row is None or row[0] != name:
                raise ValueError(f"Individual resource/texture mismatch: {name}")
            paths.append(source(corpus / row[1]))
            path_ids.append(path_id)
        entries[unlock]["nativeBottom"] = names[1]
        entries[unlock]["nativeArtwork"] = {"files": paths,
                                             "resources": ["gui/mainatlasicons/" + name for name in names],
                                             "asset": "resources.assets", "pathIds": path_ids}
    for configured in catalog.get("Offers", []):
        if selected_offers and configured["Id"] not in selected_offers:
            continue
        artwork = configured.get("Artwork")
        if not artwork:
            continue
        unlock_names = configured.get("UnlockNames", [])
        if any(name not in desired for name in unlock_names):
            raise ValueError(f"Curated offer has unknown original unlocks: {configured['Id']}")
        inventory_items = configured.get("Items", [])
        if any(item.get("Name") not in items or item.get("Count", 0) <= 0 for item in inventory_items):
            raise ValueError(f"Curated offer has unknown or empty inventory contents: {configured['Id']}")
        if not unlock_names and not inventory_items and not any(configured.get(field, 0) > 0 for field in ("Gold", "PremiumDays", "LearningPoints")):
            raise ValueError(f"Curated offer has no deliverable entitlement: {configured['Id']}")
        names = [artwork["Native"]]
        if artwork.get("Bottom"):
            names.append(artwork["Bottom"])
        paths, path_ids = [], []
        for name in names:
            if name not in resources:
                raise ValueError(f"Curated banner is outside MainAtlasIcons: {name}")
            path_id = resources[name]
            row = database.execute("SELECT name,output FROM assets WHERE source=? AND id=?", ("resources.assets", path_id)).fetchone()
            if row is None or row[0] != name:
                raise ValueError(f"Curated resource/texture mismatch: {name}")
            paths.append(corpus / row[1])
            path_ids.append(path_id)
        parts = [load_image(path) for path in paths]
        if any(part.width != parts[0].width for part in parts):
            raise ValueError(f"Original banner halves differ in width: {configured['Id']}")
        assembled = Image.new("RGBA", (parts[0].width, sum(part.height for part in parts)), (0, 0, 0, 0))
        y = 0
        for part in parts:
            assembled.paste(part, (0, y))
            y += part.height
        key = "product:" + configured["Id"]
        save(key, assembled, {"files": [source(path) for path in paths],
                             "resources": ["gui/mainatlasicons/" + name for name in names],
                             "asset": "resources.assets", "pathIds": path_ids,
                             "unlockNames": unlock_names, "theme": artwork.get("Theme", ""),
                             **({"items": inventory_items} if inventory_items else {})}, names[0])
        if len(names) == 2:
            entries[key]["nativeBottom"] = names[1]
        else:
            entries[key].pop("nativeBottom", None)
    manifest = {"version": 1, "corpus": corpus.as_posix(),
                "method": "Exact original PNGs and NGUI ItemAtlas crops; no generated or substitute art",
                "nativeClient": native_reference,
                "catalogSha256": hashlib.sha256(catalog_path.read_bytes()).hexdigest(),
                "localizedNames": {"file": "shop-names.es.json", "count": len(translated_names),
                                   "source": localization_path.relative_to(corpus).as_posix(), "tags": name_tags},
                "entries": dict(sorted(entries.items())), "missing": dict(sorted(missing.items())), "sources": dict(sorted(sources.items()))}
    content.mkdir(parents=True, exist_ok=True)
    staging = manifest_path.with_suffix(".json.tmp")
    staging.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    staging.replace(manifest_path)
    print(json.dumps({"entries": len(entries), "missing": len(missing), "localizedNames": len(translated_names), "bytes": sum((output / x["file"]).stat().st_size for x in entries.values()),
                      "manifest": str(content / "shop-art.json"), "missing_names": list(missing)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
