#!/usr/bin/env python3
"""Executable contract for check-package-dependencies.py.

Usage: scripts/test_check_package_dependencies.py [path/to/check-package-dependencies.py]

The check decides whether a published package gained a dependency, so it is held to
properties over generated packages, each built as a real nupkg (a zip holding a
nuspec) and read by the script's own `main`:
  * a set of packages, with the expectation and the version.json directories
    rendered from the same model, passes;
  * any single change to the packages, the expectation or the directories exits 1,
    and the report names the package and what differs;
  * the actual entry the report prints, pasted into the expectation, makes the
    package's own differences go away;
  * each shape the script cannot read exits 2, naming what it could not read.

HYPOTHESIS_PROFILE=explore runs many more examples, randomly; the default is
derandomized so that the flake check gives the same answer every time.
"""

import contextlib
import copy
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
import zipfile

from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st

settings.register_profile(
    "deterministic",
    derandomize=True,
    database=None,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow],
)
settings.register_profile(
    "explore",
    max_examples=2000,
    database=None,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow],
)
settings.load_profile(os.environ.get("HYPOTHESIS_PROFILE", "deterministic"))


def _load_module(path):
    spec = importlib.util.spec_from_file_location("check_package_dependencies", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


SCRIPT = (
    sys.argv.pop(1)
    if len(sys.argv) > 1
    else os.path.join(os.path.dirname(os.path.abspath(__file__)), "check-package-dependencies.py")
)
check = _load_module(SCRIPT)

# Few, similar names, so that generated packages share dependencies often, and an id
# which is a string prefix of another (`Woof`, `Woof.Core`) is never mistaken for it.
SIBLING_IDS = ["Woof", "Woof.Core", "Woof.Core.Extra", "WoofCore"]
EXTERNAL_IDS = ["FSharp.Core", "Ext", "Ext.Core", "Woof.Ext"]
NEW_IDS = ["Woof.New", "Stray"]
VERSIONS = ["1.0.0", "1.0.1", "0.12.67-g418a1b8dff", "10.1.203"]
TFMS = ["net8.0", "net10.0", "netstandard2.0"]
ATTRIBUTE_VALUES = [None, "Build,Analyzers", "Compile", "All"]
NAMESPACES = [
    None,
    "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd",
    "http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd",
]

VERSION = st.sampled_from(VERSIONS)
ATTRIBUTE = st.sampled_from(ATTRIBUTE_VALUES)


def lib_pool(package_id):
    return [
        f"lib/{tfm}/{name}"
        for tfm in ["net8.0", "net10.0"]
        for name in [f"{package_id}.dll", f"{package_id}.xml", "Moq.dll"]
    ]


def dependency(dep_id, version, include, exclude):
    """A dependency as both the nuspec model and the expectation file write it: absent attributes omitted."""
    d = {"id": dep_id}
    if version is not None:
        d["version"] = version
    if include is not None:
        d["include"] = include
    if exclude is not None:
        d["exclude"] = exclude
    return d


class World:
    """Everything one run of the script reads: nupkgs, the expectation and the repository's directories.

    `nupkgs` is a list of dicts: file, id, version, ns, bom, groups (tfm -> list of
    dependencies, each with a concrete version), lib, and the unusual shapes
    (extra_metadata, loose, untargeted, zip_override, nuspec_override, extra_entries).
    `expectation` is the JSON object the file holds, or `expectation_text` replaces it.
    """

    def __init__(self, nupkgs, expectation, dirs, split):
        self.nupkgs = nupkgs
        self.expectation = expectation
        self.expectation_text = None
        self.dirs = dirs
        self.split = split
        self.missing_directory = False

    def nupkg(self, package_id):
        return next(n for n in self.nupkgs if n["id"] == package_id)


@st.composite
def worlds(draw, need_group=False, need_sibling=False, need_external=False):
    """A consistent world: the nupkgs, the expectation and the directories all describe the same packages."""
    ids = draw(st.lists(st.sampled_from(SIBLING_IDS), min_size=2 if need_sibling else 1, max_size=4, unique=True))
    versions = {pid: draw(VERSION) for pid in ids}
    specs = {}
    for pid in ids:
        others = [i for i in ids if i != pid]
        groups = {}
        for tfm in draw(st.lists(st.sampled_from(TFMS), unique=True, max_size=2)):
            deps = {}
            for sib in draw(st.lists(st.sampled_from(others), unique=True)) if others else []:
                deps[sib] = (True, None, draw(ATTRIBUTE), draw(ATTRIBUTE))
            for ext in draw(st.lists(st.sampled_from(EXTERNAL_IDS), unique=True, max_size=3)):
                deps[ext] = (False, draw(VERSION), draw(ATTRIBUTE), draw(ATTRIBUTE))
            groups[tfm] = deps
        lib = draw(st.lists(st.sampled_from(lib_pool(pid)), unique=True))
        specs[pid] = (groups, lib)
    first = ids[0]
    groups0 = specs[first][0]
    if (need_group or need_sibling or need_external) and not groups0:
        groups0[draw(st.sampled_from(TFMS))] = {}
    if groups0:
        g = groups0[sorted(groups0)[0]]
        if need_sibling and not any(s for (s, _, _, _) in g.values()):
            g[ids[1]] = (True, None, draw(ATTRIBUTE), draw(ATTRIBUTE))
        if need_external and all(s for (s, _, _, _) in g.values()):
            g[draw(st.sampled_from(EXTERNAL_IDS))] = (False, draw(VERSION), draw(ATTRIBUTE), draw(ATTRIBUTE))

    nupkgs = []
    expectation = {}
    for pid in ids:
        groups, lib = specs[pid]
        nupkgs.append(
            {
                "file": f"{pid}.{versions[pid]}.nupkg",
                "id": pid,
                "version": versions[pid],
                "ns": draw(st.sampled_from(NAMESPACES)),
                "bom": draw(st.booleans()),
                "groups": {
                    tfm: [
                        dependency(d, versions[d] if sib else v, inc, exc)
                        for d, (sib, v, inc, exc) in deps.items()
                    ]
                    for tfm, deps in groups.items()
                },
                "lib": list(lib),
                "extra_metadata": [],
                "loose": [],
                "untargeted": [],
                "zip_override": None,
                "nuspec_override": None,
                "extra_entries": [],
                "drop_nuspec": False,
            }
        )
        expectation[pid] = {
            "lib": sorted(lib),
            "dependencies": {
                tfm: [dependency(d, None if sib else v, inc, exc) for d, (sib, v, inc, exc) in deps.items()]
                for tfm, deps in groups.items()
            },
        }
    return World(nupkgs, expectation, set(ids), draw(st.booleans()))


def _escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def _dependency_xml(d):
    attrs = "".join(f' {k}="{_escape(d[k])}"' for k in ["id", "version", "include", "exclude"] if k in d)
    return f"<dependency{attrs} />"


def nuspec_bytes(n):
    if n["nuspec_override"] is not None:
        return n["nuspec_override"]
    ns = f' xmlns="{n["ns"]}"' if n["ns"] else ""
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f"<package{ns}>",
        "  <metadata>",
        f"    <id>{_escape(n['id'])}</id>",
        f"    <version>{_escape(n['version'])}</version>",
        "    <authors>Someone</authors>",
        "    <description>A package.</description>",
    ]
    lines += ["    " + x for x in n["extra_metadata"]]
    if n["groups"] or n["loose"] or n["untargeted"]:
        lines.append("    <dependencies>")
        for tfm, deps in n["groups"].items():
            lines.append(f'      <group targetFramework="{_escape(tfm)}">')
            lines += ["        " + _dependency_xml(d) for d in deps]
            lines.append("      </group>")
        for deps in n["untargeted"]:
            lines.append("      <group>")
            lines += ["        " + _dependency_xml(d) for d in deps]
            lines.append("      </group>")
        lines += ["      " + _dependency_xml(d) for d in n["loose"]]
        lines.append("    </dependencies>")
    lines += ["  </metadata>", "</package>", ""]
    text = "\n".join(lines).encode("utf-8")
    return (b"\xef\xbb\xbf" + text) if n["bom"] else text


def nupkg_bytes(n):
    if n["zip_override"] is not None:
        return n["zip_override"]
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as z:
        z.writestr("_rels/.rels", "<Relationships />")
        if not n["drop_nuspec"]:
            z.writestr(f"{n['id']}.nuspec", nuspec_bytes(n))
        z.writestr("README.md", "readme")
        for entry in n["lib"]:
            z.writestr(entry, b"\x00library")
        for name, content in n["extra_entries"]:
            z.writestr(name, content)
        z.writestr("[Content_Types].xml", "<Types />")
        z.writestr("package/services/metadata/core-properties/a.psmdcp", "<coreProperties />")
    return buffer.getvalue()


def run(world):
    """Write `world` to a scratch directory and run the script's `main` over it; return (exit code, output)."""
    with tempfile.TemporaryDirectory() as tmp:
        root = os.path.join(tmp, "repo")
        os.makedirs(root)
        for d in world.dirs:
            os.makedirs(os.path.join(root, d))
            with open(os.path.join(root, d, "version.json"), "w") as f:
                f.write('{"version": "1.0"}\n')
        # Neither a top-level directory without a version.json nor a nested version.json is a package.
        os.makedirs(os.path.join(root, "scripts"))
        os.makedirs(os.path.join(root, "nested", "Inner"))
        with open(os.path.join(root, "nested", "Inner", "version.json"), "w") as f:
            f.write("{}\n")
        expectation_path = os.path.join(tmp, "package-dependencies.json")
        with open(expectation_path, "w") as f:
            f.write(world.expectation_text if world.expectation_text is not None else json.dumps(world.expectation))
        packed = os.path.join(tmp, "packed")
        os.makedirs(packed)
        # The layout download-artifact gives a pattern download: one directory per artifact.
        directories = []
        for i, n in enumerate(world.nupkgs):
            directory = os.path.join(packed, f"nuget-package-{i}")
            os.makedirs(directory, exist_ok=True)
            directories.append(directory)
            with open(os.path.join(directory, n["file"]), "wb") as f:
                f.write(nupkg_bytes(n))
        given = directories if world.split else [packed]
        if world.missing_directory:
            given = given + [os.path.join(tmp, "no-such-directory")]
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            code = check.main(["--expectation", expectation_path, "--root", root, *given])
        return code, out.getvalue()


def actual_entries(output):
    """The JSON object the report prints after its header."""
    start = output.index(check.ACTUAL_ENTRIES_HEADER) + len(check.ACTUAL_ENTRIES_HEADER)
    brace = output.index("{", start)
    value, _ = json.JSONDecoder().raw_decode(output, brace)
    return value


def dependency_sites(world, side, siblings=None):
    """Every (package id, tfm, index) naming a dependency on `side`; `siblings` restricts to siblings or externals."""
    ids = {n["id"] for n in world.nupkgs}
    sites = []
    for n in world.nupkgs:
        groups = n["groups"] if side == "nupkg" else world.expectation[n["id"]]["dependencies"]
        for tfm, deps in groups.items():
            for i, d in enumerate(deps):
                if siblings is None or (d["id"] in ids) == siblings:
                    sites.append((n["id"], tfm, i))
    return sorted(sites)


def groups_on(world, side, package_id):
    return world.nupkg(package_id)["groups"] if side == "nupkg" else world.expectation[package_id]["dependencies"]


def lib_on(world, side, package_id):
    return world.nupkg(package_id)["lib"] if side == "nupkg" else world.expectation[package_id]["lib"]


SIDE = st.sampled_from(["nupkg", "expectation"])


class Base(unittest.TestCase):
    def assert_difference(self, world, *names):
        code, out = run(world)
        self.assertEqual(code, 1, out)
        for name in names:
            self.assertIn(name, out)
        self.assertIn(check.RULE, out)
        return out

    def assert_unreadable(self, world, *names):
        code, out = run(world)
        self.assertEqual(code, 2, out)
        for name in names:
            self.assertIn(name, out)
        return out


class TestMatching(Base):
    def test_a_matching_set_passes(self):
        seen = {"examples": 0, "sibling": 0, "two groups": 0, "several packages": 0, "attribute": 0, "no lib": 0}

        @given(worlds())
        def prop(world):
            code, out = run(world)
            self.assertEqual(code, 0, out)
            seen["examples"] += 1
            if dependency_sites(world, "nupkg", siblings=True):
                seen["sibling"] += 1
            if any(len(n["groups"]) >= 2 for n in world.nupkgs):
                seen["two groups"] += 1
            if len(world.nupkgs) >= 2:
                seen["several packages"] += 1
            if any("exclude" in d for n in world.nupkgs for deps in n["groups"].values() for d in deps):
                seen["attribute"] += 1
            if any(not n["lib"] for n in world.nupkgs):
                seen["no lib"] += 1

        prop()
        for key, count in seen.items():
            self.assertGreater(count, seen["examples"] // 10, (key, seen))


class TestSiblings(Base):
    @given(worlds(need_sibling=True), st.data())
    def test_a_sibling_at_the_wrong_version(self, world, data):
        pid, tfm, i = data.draw(st.sampled_from(dependency_sites(world, "nupkg", siblings=True)))
        dep = world.nupkg(pid)["groups"][tfm][i]
        sibling = world.nupkg(dep["id"])
        right = sibling["version"]
        wrong = data.draw(st.sampled_from([v for v in VERSIONS + ["9.9.9"] if v != right]))
        if data.draw(st.booleans()):
            # The dependent names another version.
            dep["version"] = wrong
            self.assert_difference(world, pid, dep["id"], wrong, right)
        else:
            # The sibling was packed at another version.
            sibling["version"] = wrong
            sibling["file"] = f"{sibling['id']}.{wrong}.nupkg"
            self.assert_difference(world, pid, dep["id"], wrong, right)

    @given(worlds(need_sibling=True), st.data())
    def test_a_sibling_given_a_version_in_the_expectation_is_malformed(self, world, data):
        pid, tfm, i = data.draw(st.sampled_from(dependency_sites(world, "expectation", siblings=True)))
        dep = world.expectation[pid]["dependencies"][tfm][i]
        dep["version"] = world.nupkg(dep["id"])["version"]
        self.assert_unreadable(world, pid, dep["id"])


class TestDependencies(Base):
    @given(worlds(need_group=True), SIDE, st.data())
    def test_an_added_external_dependency(self, world, side, data):
        pid = data.draw(st.sampled_from(sorted(p for p in world.dirs if groups_on(world, side, p))))
        groups = groups_on(world, side, pid)
        tfm = data.draw(st.sampled_from(sorted(groups)))
        present = {d["id"] for d in groups[tfm]}
        new_id = data.draw(st.sampled_from([e for e in EXTERNAL_IDS + ["Moq"] if e not in present]))
        groups[tfm].append(dependency(new_id, data.draw(VERSION), data.draw(ATTRIBUTE), data.draw(ATTRIBUTE)))
        # Added to the package, it is unexpected; added to the expectation, it is missing.
        self.assert_difference(world, pid, tfm, new_id, "unexpected" if side == "nupkg" else "missing")

    @given(worlds(need_sibling=True, need_external=True), SIDE, st.data())
    def test_a_removed_dependency(self, world, side, data):
        pid, tfm, i = data.draw(st.sampled_from(dependency_sites(world, side)))
        removed = groups_on(world, side, pid)[tfm].pop(i)
        self.assert_difference(world, pid, tfm, removed["id"], "missing" if side == "nupkg" else "unexpected")

    @given(worlds(need_sibling=True, need_external=True), SIDE, st.sampled_from(["include", "exclude"]), st.data())
    def test_an_include_or_exclude_change(self, world, side, attribute, data):
        pid, tfm, i = data.draw(st.sampled_from(dependency_sites(world, side)))
        dep = groups_on(world, side, pid)[tfm][i]
        old = dep.get(attribute)
        new = data.draw(st.sampled_from([v for v in ATTRIBUTE_VALUES + ["Native"] if v != old]))
        if new is None:
            del dep[attribute]
        else:
            dep[attribute] = new
        names = [pid, tfm, dep["id"], attribute] + [v for v in [old, new] if v is not None]
        self.assert_difference(world, *names)

    @given(worlds(need_external=True), SIDE, st.data())
    def test_an_external_version_change(self, world, side, data):
        pid, tfm, i = data.draw(st.sampled_from(dependency_sites(world, side, siblings=False)))
        dep = groups_on(world, side, pid)[tfm][i]
        old = dep["version"]
        new = data.draw(st.sampled_from([v for v in VERSIONS + ["[1.0.0, )"] if v != old]))
        dep["version"] = new
        self.assert_difference(world, pid, tfm, dep["id"], old, new)

    @given(worlds(need_external=True), SIDE, st.data())
    def test_an_external_dependency_without_its_version(self, world, side, data):
        pid, tfm, i = data.draw(st.sampled_from(dependency_sites(world, side, siblings=False)))
        dep = groups_on(world, side, pid)[tfm][i]
        del dep["version"]
        if side == "nupkg":
            self.assert_difference(world, pid, tfm, dep["id"], "version")
        else:
            self.assert_unreadable(world, pid, dep["id"])


class TestGroups(Base):
    @given(worlds(), SIDE, st.data())
    def test_a_group_added(self, world, side, data):
        pid = data.draw(st.sampled_from(sorted(p for p in world.dirs if len(groups_on(world, side, p)) < len(TFMS))))
        groups = groups_on(world, side, pid)
        tfm = data.draw(st.sampled_from([t for t in TFMS if t not in groups]))
        groups[tfm] = []
        if data.draw(st.booleans()):
            groups[tfm].append(dependency("Ext", "1.0.0", None, None))
        self.assert_difference(world, pid, tfm, "unexpected" if side == "nupkg" else "missing")

    @given(worlds(need_group=True), SIDE, st.data())
    def test_a_group_removed(self, world, side, data):
        pid = data.draw(st.sampled_from(sorted(p for p in world.dirs if groups_on(world, side, p))))
        groups = groups_on(world, side, pid)
        tfm = data.draw(st.sampled_from(sorted(groups)))
        del groups[tfm]
        self.assert_difference(world, pid, tfm, "missing" if side == "nupkg" else "unexpected")


class TestLib(Base):
    @given(worlds(), SIDE, st.data())
    def test_a_changed_lib_entry(self, world, side, data):
        pid = data.draw(st.sampled_from(sorted(world.dirs)))
        lib = lib_on(world, side, pid)
        choices = ["add"] + (["remove", "rename"] if lib else [])
        choice = data.draw(st.sampled_from(choices))
        absent = [e for e in lib_pool(pid) + ["lib/net8.0/Castle.Core.dll"] if e not in lib]
        if choice == "add":
            entry = data.draw(st.sampled_from(absent))
            lib.append(entry)
            self.assert_difference(world, pid, entry, "unexpected" if side == "nupkg" else "missing")
        elif choice == "remove":
            entry = lib.pop(data.draw(st.integers(0, len(lib) - 1)))
            self.assert_difference(world, pid, entry, "missing" if side == "nupkg" else "unexpected")
        else:
            i = data.draw(st.integers(0, len(lib) - 1))
            old = lib[i]
            lib[i] = data.draw(st.sampled_from(absent))
            self.assert_difference(world, pid, old, lib[i])


class TestPackageSet(Base):
    @given(worlds(), st.data())
    def test_a_package_with_no_version_json(self, world, data):
        pid = data.draw(st.sampled_from(sorted(world.dirs)))
        world.dirs.discard(pid)
        self.assert_difference(world, pid, "version.json")

    @given(worlds(), st.sampled_from(NEW_IDS))
    def test_a_version_json_with_no_package(self, world, new_id):
        world.dirs.add(new_id)
        self.assert_difference(world, new_id, "version.json")

    @given(worlds(), st.sampled_from(NEW_IDS), st.booleans(), VERSION)
    def test_an_unexpected_package(self, world, new_id, has_directory, version):
        n = copy.deepcopy(world.nupkgs[0])
        n.update(id=new_id, version=version, file=f"{new_id}.{version}.nupkg", groups={}, lib=[])
        world.nupkgs.append(n)
        if has_directory:
            world.dirs.add(new_id)
        self.assert_difference(world, new_id, "unexpected")

    @given(worlds(), st.data())
    def test_a_missing_package(self, world, data):
        i = data.draw(st.integers(0, len(world.nupkgs) - 1))
        removed = world.nupkgs.pop(i)
        self.assert_difference(world, removed["id"], "missing")

    @given(worlds(), st.data())
    def test_a_file_named_other_than_its_id_and_version(self, world, data):
        n = data.draw(st.sampled_from(world.nupkgs))
        n["file"] = data.draw(
            st.sampled_from(
                [
                    f"{n['id'].lower()}.{n['version']}.nupkg",
                    f"{n['id']}.9.9.9.nupkg",
                    f"{n['id']}.{n['version']}.symbols.nupkg",
                    f"renamed.nupkg",
                ]
            )
        )
        self.assert_difference(world, n["id"], n["file"])


class TestUnrepresentable(Base):
    @given(worlds(), st.data())
    def test_what_the_expectation_cannot_state_is_a_difference(self, world, data):
        n = data.draw(st.sampled_from(world.nupkgs))
        shape = data.draw(st.sampled_from(["frameworkReferences", "frameworkAssemblies", "loose", "untargeted"]))
        if shape == "frameworkReferences":
            n["extra_metadata"].append(
                '<frameworkReferences><group targetFramework="net8.0">'
                '<frameworkReference name="Microsoft.AspNetCore.App" /></group></frameworkReferences>'
            )
            name = "frameworkReferences"
        elif shape == "frameworkAssemblies":
            n["extra_metadata"].append(
                '<frameworkAssemblies><frameworkAssembly assemblyName="System.Net" /></frameworkAssemblies>'
            )
            name = "frameworkAssemblies"
        elif shape == "loose":
            n["loose"].append(dependency("Moq", "4.20.72", None, None))
            name = "Moq"
        else:
            n["untargeted"].append([dependency("Moq", "4.20.72", None, None)])
            name = "targetFramework"
        self.assert_difference(world, n["id"], name)


class TestPrintedEntry(Base):
    @given(worlds(need_group=True), st.data())
    def test_the_printed_entry_is_what_the_package_holds(self, world, data):
        n = data.draw(st.sampled_from(world.nupkgs))
        change = data.draw(st.sampled_from(["add", "remove", "attribute", "version", "group", "lib"]))
        groups = n["groups"]
        if change == "add" or not any(groups.values()):
            tfm = data.draw(st.sampled_from(sorted(groups))) if groups else "net8.0"
            groups.setdefault(tfm, []).append(dependency("Moq", "4.20.72", None, "Build,Analyzers"))
        elif change == "remove":
            tfm = data.draw(st.sampled_from(sorted(t for t in groups if groups[t])))
            groups[tfm].pop()
        elif change == "attribute":
            tfm = data.draw(st.sampled_from(sorted(t for t in groups if groups[t])))
            groups[tfm][0]["include"] = "Native"
        elif change == "version":
            tfm = data.draw(st.sampled_from(sorted(t for t in groups if groups[t])))
            externals = [d for d in groups[tfm] if d["id"] not in world.dirs]
            if externals:
                externals[0]["version"] = "[2.0.0, 3.0.0)"
            else:
                groups[tfm].append(dependency("Ext", "2.0.0", None, None))
        elif change == "group":
            groups[data.draw(st.sampled_from([t for t in TFMS if t not in groups] or ["net11.0"]))] = []
        else:
            n["lib"].append("lib/net8.0/Castle.Core.dll")
        code, out = run(world)
        self.assertEqual(code, 1, out)
        printed = actual_entries(out)
        self.assertEqual(sorted(printed), [n["id"]], out)
        world.expectation.update(printed)
        code, out = run(world)
        self.assertEqual(code, 0, out)


def _zip(entries):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as z:
        for name, content in entries:
            z.writestr(name, content)
    return buffer.getvalue()


NUPKG_UNREADABLE = [
    "not a zip",
    "no nuspec",
    "nuspec only in a subdirectory",
    "two nuspecs",
    "malformed XML",
    "no id",
    "no version",
    "a group twice",
    "a dependency twice in one group",
    "a dependency with no id",
]

EXPECTATION_UNREADABLE = [
    "not JSON",
    "not an object",
    "an entry with an unknown key",
    "a dependency with an unknown key",
    "lib not a list",
    "a dependency twice in one group",
    "a dependency with no id",
]


class TestUnreadable(Base):
    @given(worlds(), st.sampled_from(NUPKG_UNREADABLE), st.data())
    def test_an_unreadable_nupkg(self, world, shape, data):
        n = data.draw(st.sampled_from(world.nupkgs))
        if shape == "not a zip":
            n["zip_override"] = b"PK\x03\x04 but not really a zip"
        elif shape == "no nuspec":
            n["drop_nuspec"] = True
        elif shape == "nuspec only in a subdirectory":
            n["drop_nuspec"] = True
            n["extra_entries"].append((f"content/{n['id']}.nuspec", nuspec_bytes(n)))
        elif shape == "two nuspecs":
            n["extra_entries"].append(("Other.nuspec", nuspec_bytes(n)))
        elif shape == "malformed XML":
            n["nuspec_override"] = nuspec_bytes(n)[:-20]
        elif shape == "no id":
            n["nuspec_override"] = nuspec_bytes(n).replace(f"<id>{n['id']}</id>".encode(), b"")
        elif shape == "no version":
            n["nuspec_override"] = nuspec_bytes(n).replace(f"<version>{n['version']}</version>".encode(), b"")
        elif shape == "a group twice":
            tfm = data.draw(st.sampled_from(TFMS))
            n["groups"].setdefault(tfm, [])
            n["nuspec_override"] = nuspec_bytes(n).replace(
                b"</dependencies>", f'  <group targetFramework="{tfm}" />\n    </dependencies>'.encode()
            )
        elif shape == "a dependency twice in one group":
            tfm = data.draw(st.sampled_from(TFMS))
            n["groups"].setdefault(tfm, []).append(dependency("Moq", "1.0.0", None, None))
            n["groups"][tfm].append(dependency("Moq", "2.0.0", None, None))
        else:
            tfm = data.draw(st.sampled_from(TFMS))
            n["groups"].setdefault(tfm, []).append({"version": "1.0.0"})
        self.assert_unreadable(world, n["file"])

    @given(worlds(), st.sampled_from(EXPECTATION_UNREADABLE), st.data())
    def test_an_unreadable_expectation(self, world, shape, data):
        pid = data.draw(st.sampled_from(sorted(world.expectation)))
        entry = world.expectation[pid]
        names = [pid]
        if shape == "not JSON":
            world.expectation_text = json.dumps(world.expectation)[:-1]
            names = []
        elif shape == "not an object":
            world.expectation_text = json.dumps(list(world.expectation))
            names = []
        elif shape == "an entry with an unknown key":
            entry["frameworkReferences"] = []
        elif shape == "a dependency with an unknown key":
            entry["dependencies"].setdefault("net8.0", []).append({"id": "Moq", "version": "1.0.0", "private": "x"})
        elif shape == "lib not a list":
            entry["lib"] = "lib/net8.0/x.dll"
        elif shape == "a dependency twice in one group":
            entry["dependencies"].setdefault("net8.0", []).extend(
                [dependency("Moq", "1.0.0", None, None), dependency("Moq", "1.0.0", None, None)]
            )
        else:
            entry["dependencies"].setdefault("net8.0", []).append({"version": "1.0.0"})
        self.assert_unreadable(world, *names)

    @given(worlds(), st.data())
    def test_two_nupkgs_with_one_id(self, world, data):
        n = copy.deepcopy(data.draw(st.sampled_from(world.nupkgs)))
        version = data.draw(st.sampled_from([v for v in VERSIONS if v != n["version"]]))
        n["version"] = version
        n["file"] = f"{n['id']}.{version}.nupkg"
        world.nupkgs.append(n)
        self.assert_unreadable(world, n["id"], n["file"])

    @given(worlds())
    def test_a_directory_that_does_not_exist(self, world):
        world.missing_directory = True
        self.assert_unreadable(world, "no-such-directory")


if __name__ == "__main__":
    unittest.main()
