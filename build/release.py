#!/usr/bin/env python3
"""Release helper for Target Board.

Reads the version and plugin details from src/TargetBoard/Properties/AssemblyInfo.cs, packs the built DLL into a zip
for NINA, and writes the plugin manifest that NINA's plugin manager reads. Used by .github/workflows/release.yml;
it can also be run by hand.

    python build/release.py version
    python build/release.py package --repo OWNER/REPO --dll path/to/TargetBoard.dll --out dist
    python build/release.py site --manifest dist/manifest.json --site path/to/nina-repo-checkout

NINA asks a plugin repository for <url>/plugins/manifests (the newest version of each plugin) and for
<url>/plugins/<identifier>/versions (every version of one plugin). The site command keeps both files up to date.
"""

import argparse
import hashlib
import json
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSEMBLY_INFO = os.path.join(ROOT, "src", "TargetBoard", "Properties", "AssemblyInfo.cs")
CHANGELOG = os.path.join(ROOT, "CHANGELOG.md")

# Limits from NINA 3.2's manifest schema (NINA.Plugin/ManifestDefinition/PluginManifest.cs).
MAX_NAME, MAX_SHORT, MAX_LONG, MAX_TAG, MAX_TAGS = 50, 256, 10000, 30, 20


# ---------------------------------------------------------------- reading AssemblyInfo.cs

def _string_literal(text, i):
    """Reads one C# string literal starting at text[i]; returns (value, index after it)."""
    if text.startswith('@"', i):
        i += 2
        out = []
        while True:
            j = text.index('"', i)
            out.append(text[i:j])
            if text.startswith('""', j):
                out.append('"')
                i = j + 2
            else:
                return "".join(out), j + 1
    if text[i] != '"':
        raise ValueError(f"expected a string at offset {i}")
    i += 1
    out = []
    escapes = {"n": "\n", "r": "\r", "t": "\t", "0": "\0", "\\": "\\", '"': '"', "'": "'"}
    while text[i] != '"':
        if text[i] == "\\":
            c = text[i + 1]
            if c == "u":
                out.append(chr(int(text[i + 2:i + 6], 16)))
                i += 6
                continue
            out.append(escapes.get(c, c))
            i += 2
        else:
            out.append(text[i])
            i += 1
    return "".join(out), i + 1


def read_assembly_info(path=ASSEMBLY_INFO):
    """Returns (attributes, metadata): plain attributes by name, and AssemblyMetadata values by key."""
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    # Drop // comments so commented-out attributes are not read.
    text = re.sub(r"^\s*//.*$", "", text, flags=re.M)
    attributes, metadata = {}, {}
    for m in re.finditer(r"\[assembly:\s*(\w+?)(?:Attribute)?\s*\(", text):
        name, i, args = m.group(1), m.end(), []
        while True:
            while text[i] in " \t\r\n,":
                i += 1
            if text[i] == ")":
                break
            if text[i] == '"' or text.startswith('@"', i):
                value, i = _string_literal(text, i)
                args.append(value)
            else:
                # Not a string (ComVisible(false) and the like): skip to the next argument.
                j = i
                while text[j] not in ",)":
                    j += 1
                args.append(text[i:j].strip())
                i = j
        if name == "AssemblyMetadata" and len(args) == 2:
            metadata[args[0]] = args[1]
        elif args:
            attributes[name] = args[0]
    return attributes, metadata


def _version_parts(text, what):
    parts = text.strip().split(".")
    if len(parts) != 4 or not all(p.isdigit() for p in parts):
        sys.exit(f"{what} must have four numbers, like 1.4.0.0; found '{text}'")
    return [int(p) for p in parts]


def version():
    attributes, _ = read_assembly_info()
    return ".".join(str(p) for p in _version_parts(attributes["AssemblyVersion"], "AssemblyVersion"))


def release_notes(ver, repo):
    """The CHANGELOG section for this version, plus how to get updates in NINA."""
    notes = ""
    if os.path.exists(CHANGELOG):
        with open(CHANGELOG, encoding="utf-8") as f:
            text = f.read()
        m = re.search(rf"^## {re.escape(ver)}\s*$(.*?)(?=^## |\Z)", text, flags=re.M | re.S)
        if m:
            notes = m.group(1).strip()
    if not notes:
        notes = "See CHANGELOG.md."
    return (f"{notes}\n\n---\n\n**Updates in NINA.** In NINA, open Options → General → Plugin Repositories, press +, and add\n\n"
            f"```\nhttps://raw.githubusercontent.com/{repo}/nina-repo\n```\n\n"
            "NINA's Plugins tab then offers each new version as an update.\n")


# ---------------------------------------------------------------- package

def build_manifest(repo, zip_url, checksum):
    attributes, meta = read_assembly_info()
    repo_url = f"https://github.com/{repo}"
    v = _version_parts(attributes["AssemblyVersion"], "AssemblyVersion")
    mv = _version_parts(meta["MinimumApplicationVersion"], "MinimumApplicationVersion")
    as_version = lambda p: {"Major": p[0], "Minor": p[1], "Patch": p[2], "Build": p[3]}
    tags = [t.strip() for t in meta.get("Tags", "").split(",") if t.strip()]

    descriptions = {
        "ShortDescription": attributes.get("AssemblyDescription", ""),
        "LongDescription": meta.get("LongDescription", ""),
    }
    for key in ("FeaturedImageURL", "ScreenshotURL", "AltScreenshotURL"):
        if meta.get(key):
            descriptions[key] = meta[key]

    manifest = {
        "Name": attributes["AssemblyTitle"],
        "Identifier": attributes["Guid"],
        "Version": as_version(v),
        "Author": attributes.get("AssemblyCompany", ""),
        "Homepage": meta.get("Homepage") or repo_url,
        "Repository": meta.get("Repository") or repo_url,
        "License": meta.get("License", ""),
        "LicenseURL": meta.get("LicenseURL", ""),
        "ChangelogURL": meta.get("ChangelogURL") or f"{repo_url}/blob/main/CHANGELOG.md",
        "Tags": tags,
        "MinimumApplicationVersion": as_version(mv),
        "Descriptions": descriptions,
        "Installer": {"URL": zip_url, "Type": "ARCHIVE", "Checksum": checksum, "ChecksumType": "SHA256"},
    }

    problems = []
    if len(manifest["Name"]) > MAX_NAME: problems.append(f"Name is over {MAX_NAME} characters")
    if not re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", manifest["Identifier"]):
        problems.append("Guid is not a GUID")
    if not manifest["Author"]: problems.append("AssemblyCompany (the author) is empty")
    if not manifest["License"] or not manifest["LicenseURL"]: problems.append("License or LicenseURL is empty")
    if not descriptions["ShortDescription"]: problems.append("AssemblyDescription is empty")
    if len(descriptions["ShortDescription"]) > MAX_SHORT: problems.append(f"AssemblyDescription is over {MAX_SHORT} characters")
    if len(descriptions["LongDescription"]) > MAX_LONG: problems.append(f"LongDescription is over {MAX_LONG} characters")
    if len(tags) > MAX_TAGS or any(len(t) > MAX_TAG for t in tags): problems.append(f"at most {MAX_TAGS} tags of {MAX_TAG} characters")
    if problems:
        sys.exit("The plugin details in AssemblyInfo.cs would not pass NINA's checks: " + "; ".join(problems))
    return manifest


def package(args):
    ver = version()
    if not os.path.isfile(args.dll):
        sys.exit(f"Build the plugin first: {args.dll} not found")
    os.makedirs(args.out, exist_ok=True)
    zip_name = f"TargetBoard-{ver}.zip"
    zip_path = os.path.join(args.out, zip_name)
    # NINA unpacks the zip into Plugins\3.0.0\<plugin name>\, so the files go at the top of the zip.
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(args.dll, os.path.basename(args.dll))
        pdb = os.path.splitext(args.dll)[0] + ".pdb"
        if os.path.isfile(pdb):
            z.write(pdb, os.path.basename(pdb))
    with open(zip_path, "rb") as f:
        checksum = hashlib.sha256(f.read()).hexdigest()

    zip_url = f"https://github.com/{args.repo}/releases/download/v{ver}/{zip_name}"
    manifest = build_manifest(args.repo, zip_url, checksum)
    with open(os.path.join(args.out, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    with open(os.path.join(args.out, "notes.md"), "w", encoding="utf-8") as f:
        f.write(release_notes(ver, args.repo))
    print(f"{zip_path}  sha256 {checksum}")


# ---------------------------------------------------------------- site

def _key(m):
    v = m["Version"]
    return tuple(int(v[k]) for k in ("Major", "Minor", "Patch", "Build"))


def _load(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        text = f.read().strip()
    return json.loads(text) if text else []


def _save(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def site(args):
    with open(args.manifest, encoding="utf-8") as f:
        new = json.load(f)
    ident = new["Identifier"]

    # Every version of this plugin, newest first, for NINA's version list.
    versions_path = os.path.join(args.site, "plugins", ident, "versions")
    versions = [m for m in _load(versions_path) if _key(m) != _key(new)] + [new]
    versions.sort(key=_key, reverse=True)
    _save(versions_path, versions)

    # The newest version of each plugin in this repository.
    manifests_path = os.path.join(args.site, "plugins", "manifests")
    manifests = [m for m in _load(manifests_path) if m.get("Identifier", "").lower() != ident.lower()] + [versions[0]]
    _save(manifests_path, manifests)

    readme = "# NINA plugin repository\n\nThe release workflow writes this branch; don't edit it by hand.\n"
    if args.repo:
        readme += ("\nIn NINA, open Options → General → Plugin Repositories, press +, and add\n\n"
                   f"```\nhttps://raw.githubusercontent.com/{args.repo}/nina-repo\n```\n")
    with open(os.path.join(args.site, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(readme)
    print(f"{new['Name']} {'.'.join(map(str, _key(new)))}: {len(versions)} version(s) listed")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    sub.add_parser("version", help="print the plugin version")
    pk = sub.add_parser("package", help="zip the DLL and write manifest.json and notes.md")
    pk.add_argument("--repo", required=True, help="OWNER/REPO on GitHub")
    pk.add_argument("--dll", required=True)
    pk.add_argument("--out", default="dist")
    st = sub.add_parser("site", help="add a manifest to the plugin repository files")
    st.add_argument("--manifest", required=True)
    st.add_argument("--site", required=True)
    st.add_argument("--repo", default="")
    args = p.parse_args()
    if args.command == "version":
        print(version())
    elif args.command == "package":
        package(args)
    else:
        site(args)


if __name__ == "__main__":
    main()
