#!/usr/bin/env python3
"""Check that each published package depends on exactly what scripts/package-dependencies.json says.

A published package's dependencies are what its consumers must install, and a
dependency used only by tests must never become one. Nothing else in CI reads a
package's dependency list: the footprints deal in repository paths, so a project that
gains a PackageReference changes none of them. This script reads the packages
themselves.

Usage:
  scripts/check-package-dependencies.py [--expectation FILE] [--root DIR] DIRECTORY...

It reads every `*.nupkg` beneath the directories given (CI passes the directory into
which it downloads every `nuget-package-*` artifact; locally, the output of
`dotnet pack --configuration Release -o <dir>`), and compares three things with the
expectation (by default scripts/package-dependencies.json, beside this script):
  1. Each nuspec's dependency groups: for each targetFramework, the set of
     dependencies, each with its id, its version and its include and exclude
     attributes exactly as written. Order is ignored; an extra or missing group is a
     difference.
  2. The nupkg's entries whose path begins `lib/`, as a set. A third-party assembly
     packed beside ours reaches consumers without appearing as a dependency.
  3. The set of packages. The package ids found must be exactly the expectation's
     keys, each found once, and the expectation's keys must be exactly the names of
     the top-level directories of the repository (by default this script's parent
     directory) that hold a version.json. That takes a package's directory name to be
     its PackageId, which is true of every package today.

A dependency whose id is itself a key of the expectation is a *sibling*: one of this
repository's own packages, whose version Nerdbank.GitVersioning computes per commit.
The expectation gives a sibling no version, and the check instead requires the
version in the nuspec to equal the version of the sibling's own nupkg among those
given. Every other dependency is *external*, and the expectation states its version
exactly, so that a change to the floor consumers must meet is visible in review.

A nuspec with a `frameworkReferences` or `frameworkAssemblies` element, a dependency
outside a group, or a group with no targetFramework is reported as a difference: the
expectation has no way to state one. The nuspec is read by local element names,
because its XML namespace varies with the features a package uses. Each file must be
named `<id>.<version>.nupkg`, as the release job derives a tag from the file name.

The expectation is maintained by hand, and this script has no mode that writes it: it
is a statement of policy, not something derived. A failing run prints the actual
entry of each package that differs in the file's own format, so that a wanted change
is a small edit made in the same commit as the change to the project.

Exit status: 0 when everything matches; 1 when the packages were read and differ from
the expectation (including an unexpected or missing package); 2 when the question
could not be answered (a file that is not a zip, a nupkg with no nuspec or with two,
an unparseable nuspec, two nupkgs with one id, a malformed expectation, a directory
that does not exist).

WHAT THIS CANNOT SEE:
  * A PackageReference with PrivateAssets="all" whose assembly our code nevertheless
    references. It is absent from the nuspec, and the shipped assembly still needs it
    at run time (#2324).
  * Anything outside `lib/` and the dependency groups, such as `build/` or `tools/`
    entries. None of our packages has any today.
"""

import argparse
import dataclasses
import json
import os
import sys
import xml.etree.ElementTree as ET
import zipfile
from typing import Dict, FrozenSet, List, Mapping, Optional, Sequence, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXPECTATION = os.path.join(HERE, "package-dependencies.json")
DEFAULT_ROOT = os.path.dirname(HERE)

RULE = (
    "A published package's dependencies are what its consumers must install, and a\n"
    "dependency used only by tests must never become one. If this change is meant for\n"
    "consumers, edit scripts/package-dependencies.json in the same commit. Otherwise\n"
    "remove the reference from the project."
)

ACTUAL_ENTRIES_HEADER = "What the packages which differ hold, as scripts/package-dependencies.json states it:"

ENTRY_KEYS = frozenset({"lib", "dependencies"})
DEPENDENCY_KEYS = ("id", "version", "include", "exclude")
FRAMEWORK_ELEMENTS = ("frameworkReferences", "frameworkAssemblies")


class Unreadable(Exception):
    """The question could not be answered: an input could not be read as what it should be."""


@dataclasses.dataclass(frozen=True)
class Dependency:
    id: str
    version: Optional[str]
    include: Optional[str]
    exclude: Optional[str]


# For each targetFramework, its dependencies by id.
Groups = Mapping[str, Mapping[str, Dependency]]


@dataclasses.dataclass(frozen=True)
class Package:
    """What one nupkg holds, as far as the check compares it."""

    path: str
    id: str
    version: str
    groups: Groups
    lib: FrozenSet[str]
    # Each thing in the nuspec which the expectation cannot state, described.
    unstatable: Tuple[str, ...]

    @property
    def file_name(self) -> str:
        return os.path.basename(self.path)


@dataclasses.dataclass(frozen=True)
class Entry:
    """One package's entry in the expectation. A sibling dependency has no version."""

    lib: FrozenSet[str]
    groups: Groups


@dataclasses.dataclass(frozen=True)
class Difference:
    line: str
    # The package whose actual entry the report should print, if any.
    package: Optional[str]


def _reject_duplicate_keys(pairs: List[Tuple[str, object]]) -> Dict[str, object]:
    result: Dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise Unreadable(f"the key {json.dumps(key)} appears twice in one object")
        result[key] = value
    return result


def parse_expectation(text: str) -> Dict[str, Entry]:
    """Parse the expectation file's contents; raise Unreadable if it is malformed."""
    try:
        document = json.loads(text, object_pairs_hook=_reject_duplicate_keys)
    except json.JSONDecodeError as e:
        raise Unreadable(f"not JSON: {e}") from e
    if not isinstance(document, dict):
        raise Unreadable("must hold a JSON object, from package id to entry")
    siblings = frozenset(document)
    return {package_id: _parse_entry(package_id, value, siblings) for package_id, value in document.items()}


def _parse_entry(package_id: str, value: object, siblings: FrozenSet[str]) -> Entry:
    def bad(why: str) -> Unreadable:
        return Unreadable(f"the entry for {package_id}: {why}")

    if not isinstance(value, dict) or set(value) != ENTRY_KEYS:
        raise bad(f"must be an object with exactly the keys {sorted(ENTRY_KEYS)}")
    lib = value["lib"]
    if not isinstance(lib, list) or not all(isinstance(e, str) and e.startswith("lib/") for e in lib):
        raise bad('"lib" must be a list of paths, each beginning "lib/"')
    if len(set(lib)) != len(lib):
        raise bad('"lib" names an entry twice')
    dependencies = value["dependencies"]
    if not isinstance(dependencies, dict):
        raise bad('"dependencies" must be an object, from targetFramework to a list of dependencies')
    groups: Dict[str, Dict[str, Dependency]] = {}
    for tfm, deps in dependencies.items():
        if not tfm or not isinstance(deps, list):
            raise bad(f"the group {json.dumps(tfm)} must have a non-empty name and hold a list of dependencies")
        group: Dict[str, Dependency] = {}
        for d in deps:
            if (
                not isinstance(d, dict)
                or "id" not in d
                or not set(d) <= set(DEPENDENCY_KEYS)
                or not all(isinstance(v, str) for v in d.values())
            ):
                raise bad(
                    f"in group {tfm}, {json.dumps(d)}: a dependency must be an object of strings, "
                    f"with an id and no keys but {list(DEPENDENCY_KEYS)}"
                )
            dep_id = d["id"]
            if dep_id in group:
                raise bad(f"in group {tfm}, the dependency {dep_id} appears twice")
            if dep_id in siblings and "version" in d:
                raise bad(
                    f"in group {tfm}, the dependency {dep_id} is a sibling package, so it must not state a "
                    "version: the check requires the version of the sibling's own nupkg"
                )
            if dep_id not in siblings and "version" not in d:
                raise bad(f"in group {tfm}, the external dependency {dep_id} must state its version")
            group[dep_id] = Dependency(dep_id, d.get("version"), d.get("include"), d.get("exclude"))
        groups[tfm] = group
    return Entry(frozenset(lib), groups)


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _children(element: ET.Element, name: str) -> List[ET.Element]:
    return [c for c in element if _local(c.tag) == name]


def _single_text(metadata: ET.Element, name: str, path: str) -> str:
    found = _children(metadata, name)
    if len(found) != 1 or not (found[0].text or "").strip():
        raise Unreadable(f"{path}: the nuspec must have exactly one non-empty <{name}>")
    return (found[0].text or "").strip()


def _dependency(element: ET.Element, path: str, unstatable: List[str]) -> Dependency:
    dep_id = element.get("id")
    if not dep_id:
        raise Unreadable(f"{path}: the nuspec has a <dependency> with no id")
    others = sorted(set(element.attrib) - set(DEPENDENCY_KEYS))
    if others:
        unstatable.append(f"the dependency {dep_id} has attributes {others}, which the expectation cannot state")
    return Dependency(dep_id, element.get("version"), element.get("include"), element.get("exclude"))


def parse_nupkg(path: str, names: Sequence[str], nuspec: bytes) -> Package:
    """Read a package from its zip entry names and its nuspec's bytes; raise Unreadable if they cannot be."""
    try:
        root = ET.fromstring(nuspec)
    except ET.ParseError as e:
        raise Unreadable(f"{path}: the nuspec is not well-formed XML: {e}") from e
    metadatas = _children(root, "metadata")
    if _local(root.tag) != "package" or len(metadatas) != 1:
        raise Unreadable(f"{path}: the nuspec must be a <package> holding one <metadata>")
    metadata = metadatas[0]
    package_id = _single_text(metadata, "id", path)
    version = _single_text(metadata, "version", path)
    unstatable: List[str] = []
    for name in FRAMEWORK_ELEMENTS:
        if _children(metadata, name):
            unstatable.append(f"the nuspec has a <{name}> element, which the expectation cannot state")
    dependencies = _children(metadata, "dependencies")
    if len(dependencies) > 1:
        raise Unreadable(f"{path}: the nuspec has more than one <dependencies>")
    groups: Dict[str, Dict[str, Dependency]] = {}
    for child in dependencies[0] if dependencies else []:
        kind = _local(child.tag)
        if kind == "dependency":
            d = _dependency(child, path, unstatable)
            unstatable.append(f"the dependency {d.id} is outside any group, which the expectation cannot state")
        elif kind != "group":
            unstatable.append(f"<dependencies> holds a <{kind}>, which the expectation cannot state")
        else:
            tfm = child.get("targetFramework")
            group: Dict[str, Dependency] = {}
            for grandchild in child:
                if _local(grandchild.tag) != "dependency":
                    unstatable.append(f"a group holds a <{_local(grandchild.tag)}>, which the expectation cannot state")
                    continue
                d = _dependency(grandchild, path, unstatable)
                if d.id in group:
                    raise Unreadable(f"{path}: the nuspec names the dependency {d.id} twice in one group")
                group[d.id] = d
            if tfm is None:
                unstatable.append(
                    f"a dependency group with no targetFramework (holding {sorted(group)}), "
                    "which the expectation cannot state"
                )
            elif tfm in groups:
                raise Unreadable(f"{path}: the nuspec has two groups for targetFramework {tfm}")
            else:
                groups[tfm] = group
    lib = frozenset(n for n in names if n.startswith("lib/") and not n.endswith("/"))
    return Package(path, package_id, version, groups, lib, tuple(unstatable))


def read_nupkg(path: str) -> Package:
    """Read the nupkg at `path`; raise Unreadable if it cannot be read as one."""
    try:
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            nuspecs = [n for n in names if "/" not in n and n.lower().endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise Unreadable(f"{path}: a nupkg must hold exactly one .nuspec at its root, but holds {nuspecs}")
            nuspec = z.read(nuspecs[0])
    except (zipfile.BadZipFile, OSError) as e:
        raise Unreadable(f"{path}: not a readable zip: {e}") from e
    return parse_nupkg(path, names, nuspec)


def read_packages(directories: Sequence[str]) -> List[Package]:
    """Every nupkg beneath `directories`; raise Unreadable on a missing directory or two nupkgs with one id."""
    paths = []
    for directory in directories:
        if not os.path.isdir(directory):
            raise Unreadable(f"{directory}: no such directory")
        for dirpath, dirnames, filenames in os.walk(directory):
            dirnames.sort()
            paths.extend(os.path.join(dirpath, f) for f in sorted(filenames) if f.endswith(".nupkg"))
    seen = set()
    packages = []
    for path in paths:
        real = os.path.realpath(path)
        if real not in seen:
            seen.add(real)
            packages.append(read_nupkg(path))
    by_id: Dict[str, Package] = {}
    for p in packages:
        if p.id in by_id:
            raise Unreadable(f"two nupkgs have the id {p.id}: {by_id[p.id].path} and {p.path}")
        by_id[p.id] = p
    return packages


def package_directories(root: str) -> FrozenSet[str]:
    """The top-level directories of `root` that hold a version.json."""
    try:
        names = os.listdir(root)
    except OSError as e:
        raise Unreadable(f"{root}: cannot list the repository's directories: {e}") from e
    return frozenset(n for n in names if os.path.isfile(os.path.join(root, n, "version.json")))


def _show(value: Optional[str]) -> str:
    return "absent" if value is None else json.dumps(value)


def _describe(d: Dependency) -> str:
    fields = [("version", d.version), ("include", d.include), ("exclude", d.exclude)]
    return d.id + "".join(f" {k}={json.dumps(v)}" for k, v in fields if v is not None)


def _compare_package(
    expected: Entry, actual: Package, siblings: FrozenSet[str], versions: Mapping[str, str]
) -> List[str]:
    """The differences between one package and its entry, each a line without the package's name."""
    lines = list(actual.unstatable)
    for tfm in sorted(set(actual.groups) - set(expected.groups)):
        lines.append(f"unexpected dependency group {tfm}")
    for tfm in sorted(set(expected.groups) - set(actual.groups)):
        lines.append(f"missing dependency group {tfm}")
    for tfm in sorted(set(expected.groups) & set(actual.groups)):
        want = expected.groups[tfm]
        have = actual.groups[tfm]
        for dep_id in sorted(set(have) - set(want)):
            lines.append(f"{tfm}: unexpected dependency {_describe(have[dep_id])}")
        for dep_id in sorted(set(want) - set(have)):
            lines.append(f"{tfm}: missing dependency {_describe(want[dep_id])}")
        for dep_id in sorted(set(want) & set(have)):
            w = want[dep_id]
            h = have[dep_id]
            if dep_id in siblings:
                # A sibling: the expectation states no version, and the sibling's own nupkg decides it.
                sibling_version = versions.get(dep_id)
                if sibling_version is None:
                    lines.append(
                        f"{tfm}: dependency {dep_id} is a sibling package, but no nupkg of {dep_id} was given, "
                        f"so its version {_show(h.version)} names nothing this run produced"
                    )
                elif h.version != sibling_version:
                    lines.append(
                        f"{tfm}: dependency {dep_id} has version {_show(h.version)}, but the {dep_id} nupkg given "
                        f"has version {json.dumps(sibling_version)}"
                    )
            elif h.version != w.version:
                lines.append(f"{tfm}: dependency {dep_id}: version is {_show(h.version)}, expected {_show(w.version)}")
            for attribute, hv, wv in [("include", h.include, w.include), ("exclude", h.exclude, w.exclude)]:
                if hv != wv:
                    lines.append(f"{tfm}: dependency {dep_id}: {attribute} is {_show(hv)}, expected {_show(wv)}")
    for entry in sorted(actual.lib - expected.lib):
        lines.append(f"unexpected lib/ entry {entry}")
    for entry in sorted(expected.lib - actual.lib):
        lines.append(f"missing lib/ entry {entry}")
    return lines


def compare(
    expectation: Mapping[str, Entry], packages: Sequence[Package], directories: FrozenSet[str]
) -> List[Difference]:
    """Every difference between the packages read and the expectation, in a stable order."""
    by_id = {p.id: p for p in packages}
    assert len(by_id) == len(packages), "read_packages refuses two nupkgs with one id"
    # Each sibling's version is the version of its own nupkg among those given.
    versions = {p.id: p.version for p in packages if p.id in expectation}
    differences: List[Difference] = []
    for d in sorted(directories - set(expectation)):
        differences.append(
            Difference(
                f"{d}/version.json makes {d} a package, but scripts/package-dependencies.json names no package {d} "
                "(the check takes a package's directory name to be its PackageId)",
                None,
            )
        )
    for package_id in sorted(set(expectation) - directories):
        differences.append(
            Difference(
                f"{package_id}: scripts/package-dependencies.json names it, but no top-level directory "
                f"{package_id} holds a version.json "
                "(the check takes a package's directory name to be its PackageId)",
                None,
            )
        )
    for package_id in sorted(set(expectation) - set(by_id)):
        differences.append(Difference(f"{package_id}: missing package: no nupkg of it was given", None))
    for p in sorted(packages, key=lambda p: p.id):
        prefix = f"{p.id} {p.version}"
        expected_name = f"{p.id}.{p.version}.nupkg"
        if p.file_name != expected_name:
            line = f"{prefix}: the file is named {p.file_name}, but must be named {expected_name}"
            differences.append(Difference(line, None))
        if p.id not in expectation:
            line = f"{prefix}: unexpected package ({p.file_name}): scripts/package-dependencies.json names no such package"
            differences.append(Difference(line, p.id))
            continue
        for line in _compare_package(expectation[p.id], p, frozenset(expectation), versions):
            differences.append(Difference(f"{prefix}: {line}", p.id))
    return differences


def render_entry(package: Package, siblings: FrozenSet[str]) -> Dict[str, object]:
    """The package's entry as the expectation file states it: a sibling without its version."""

    def dependency(d: Dependency) -> Dict[str, str]:
        version = None if d.id in siblings else d.version
        fields = [("id", d.id), ("version", version), ("include", d.include), ("exclude", d.exclude)]
        return {k: v for k, v in fields if v is not None}

    groups = sorted(package.groups.items())
    return {
        "lib": sorted(package.lib),
        "dependencies": {tfm: [dependency(group[dep_id]) for dep_id in sorted(group)] for tfm, group in groups},
    }


def format_entries(entries: Mapping[str, Mapping[str, object]]) -> str:
    """Entries as JSON in the expectation file's layout: one line for each lib/ entry and each dependency."""

    def items(opening: str, values: Sequence[str], indent: str, last: bool) -> List[str]:
        """A JSON list of already-rendered values, one to a line, after `opening`."""
        comma = "" if last else ","
        if not values:
            return [f"{indent}{opening}[]{comma}"]
        body = [f"{indent}  {v}" + ("," if i < len(values) - 1 else "") for i, v in enumerate(values)]
        return [f"{indent}{opening}["] + body + [f"{indent}]{comma}"]

    lines = ["{"]
    for k, (package_id, e) in enumerate(entries.items()):
        lib = e["lib"]
        dependencies = e["dependencies"]
        assert isinstance(lib, list) and isinstance(dependencies, dict)
        lines.append(f"  {json.dumps(package_id)}: {{")
        lines += items('"lib": ', [json.dumps(x) for x in lib], "    ", last=False)
        if dependencies:
            lines.append('    "dependencies": {')
            for j, (tfm, deps) in enumerate(dependencies.items()):
                last = j == len(dependencies) - 1
                lines += items(f"{json.dumps(tfm)}: ", [json.dumps(d) for d in deps], "      ", last)
            lines.append("    }")
        else:
            lines.append('    "dependencies": {}')
        lines.append("  }" + ("," if k < len(entries) - 1 else ""))
    return "\n".join(lines + ["}"])


def main(argv: Sequence[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--expectation", default=DEFAULT_EXPECTATION, help="the expectation file")
    parser.add_argument(
        "--root", default=DEFAULT_ROOT, help="the repository, whose top-level directories with a version.json are packages"
    )
    parser.add_argument("directories", nargs="+", help="directories beneath which every *.nupkg is read")
    args = parser.parse_args(argv)
    try:
        try:
            with open(args.expectation, encoding="utf-8") as f:
                text = f.read()
        except OSError as e:
            raise Unreadable(f"cannot read the expectation: {e}") from e
        try:
            expectation = parse_expectation(text)
        except Unreadable as e:
            raise Unreadable(f"{args.expectation}: {e}") from e
        directories = package_directories(args.root)
        packages = read_packages(args.directories)
    except Unreadable as e:
        print(f"error: {e}", file=sys.stderr)
        return 2
    differences = compare(expectation, packages, directories)
    if not differences:
        print(f"All {len(packages)} packages depend on exactly what {args.expectation} says.")
        return 0
    for d in differences:
        print(d.line)
    by_id = {p.id: p for p in packages}
    differing = sorted({d.package for d in differences if d.package is not None})
    if differing:
        siblings = frozenset(expectation)
        entries = {package_id: render_entry(by_id[package_id], siblings) for package_id in differing}
        print()
        print(ACTUAL_ENTRIES_HEADER)
        print(format_entries(entries))
    print()
    print(RULE)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
