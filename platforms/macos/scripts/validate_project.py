#!/usr/bin/env python3
"""Check native project membership and bundled resources without a Mac SDK.

This is an optional source-tree check using only the Python standard library.
It does not compile Swift, validate Apple APIs, or replace xcodebuild tests.
"""
from pathlib import Path
import json
import plistlib
import re
import struct
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]

class OpenStepParser:
    def __init__(self, text):
        pattern = r'"(?:\\.|[^"\\])*"|/\*.*?\*/|//[^\n]*|[{}()=;,]|[^\s{}()=;,]+'
        self.tokens = [
            token for token in re.findall(pattern, text, re.DOTALL)
            if not token.startswith(("/*", "//"))
        ]
        self.index = 0

    def take(self, expected=None):
        if self.index >= len(self.tokens):
            raise ValueError("Unexpected end of project.")
        value = self.tokens[self.index]
        self.index += 1
        if expected is not None and value != expected:
            raise ValueError(f"Expected {expected!r}, found {value!r}.")
        return value

    def parse(self):
        token = self.take()
        if token == "{":
            result = {}
            while self.tokens[self.index] != "}":
                key = self.parse()
                if key in result:
                    raise ValueError(f"Duplicate project key: {key}.")
                self.take("=")
                result[key] = self.parse()
                self.take(";")
            self.take("}")
            return result
        if token == "(":
            result = []
            while self.tokens[self.index] != ")":
                result.append(self.parse())
                if self.tokens[self.index] != ")":
                    self.take(",")
            self.take(")")
            return result
        if token.startswith('"'):
            return json.loads(token)
        return token

def require(condition, message):
    if not condition:
        raise ValueError(message)

def main():
    parser = OpenStepParser((ROOT / "Boardcast.xcodeproj/project.pbxproj").read_text(encoding="utf-8"))
    document = parser.parse()
    require(parser.index == len(parser.tokens), "Extra tokens after project.")
    objects = document["objects"]
    project = objects[document["rootObject"]]
    require(project["isa"] == "PBXProject", "Root must be a PBXProject.")
    def references(value):
        if isinstance(value, dict):
            for child in value.values():
                yield from references(child)
        elif isinstance(value, list):
            for child in value:
                yield from references(child)
        elif re.fullmatch(r"[A-F0-9]{24}", value):
            yield value
    require(set(references(document)) <= set(objects), "The project contains unresolved object references.")
    paths = {}
    def visit(key, parent):
        obj = objects[key]
        path = parent / obj.get("path", "")
        if obj["isa"] in ("PBXGroup", "PBXVariantGroup"):
            for child in obj["children"]:
                visit(child, path)
        elif obj["isa"] == "PBXFileReference":
            if obj["sourceTree"] == "<group>":
                require(path.exists(), f"Missing referenced file: {path.relative_to(ROOT)}.")
                paths[key] = path
    visit(project["mainGroup"], ROOT)
    target_ids = {objects[key]["name"]: key for key in project["targets"]}
    targets = {name: objects[key] for name, key in target_ids.items()}
    require(set(targets) == {"Boardcast", "BoardcastUpdater", "BoardcastTests"}, "Expected app, embedded native updater, and XCTest targets.")
    memberships = {}
    for name, directories in {
        "Boardcast": ["Sources/Boardcast", "Sources/Shared"],
        "BoardcastUpdater": ["Sources/BoardcastUpdater", "Sources/Shared"],
        "BoardcastTests": ["Tests/BoardcastTests"],
    }.items():
        target = targets[name]
        phases = [objects[key] for key in target["buildPhases"] if objects[key]["isa"] == "PBXSourcesBuildPhase"]
        require(len(phases) == 1, f"{name} must have one source phase.")
        members = [paths[objects[key]["fileRef"]] for key in phases[0]["files"]]
        expected = {file for directory in directories for file in (ROOT / directory).rglob("*.swift")}
        require(set(members) == expected and len(members) == len(expected), f"{name} Swift source membership is missing or duplicated.")
        memberships[name] = members
    app = targets["Boardcast"]
    helper = targets["BoardcastUpdater"]
    require(app["productType"] == "com.apple.product-type.application", "Boardcast must be an app.")
    require(helper["productType"] == "com.apple.product-type.tool", "The updater must be a native command-line executable.")
    require(objects[helper["productReference"]]["path"] == "BoardcastUpdater", "The helper product name must be stable.")
    dependency_targets = {objects[key]["target"] for key in app["dependencies"]}
    require(target_ids["BoardcastUpdater"] in dependency_targets, "The app must build its embedded updater.")
    test_dependency_targets = {objects[key]["target"] for key in targets["BoardcastTests"]["dependencies"]}
    require(target_ids["Boardcast"] in test_dependency_targets, "Hosted XCTest must depend on the app.")
    copy_phases = [objects[key] for key in app["buildPhases"] if objects[key]["isa"] == "PBXCopyFilesBuildPhase"]
    require(len(copy_phases) == 1, "Embed the helper with one standard Copy Files phase.")
    copy_phase = copy_phases[0]
    require(copy_phase["dstSubfolderSpec"] == "6" and copy_phase["dstPath"] == "", "The helper must live in Contents/MacOS.")
    require(len(copy_phase["files"]) == 1, "Only the updater executable belongs in the helper copy phase.")
    embedded = objects[copy_phase["files"][0]]
    require(embedded["fileRef"] == helper["productReference"], "Copy the built helper product.")
    require(set(embedded["settings"]["ATTRIBUTES"]) == {"CodeSignOnCopy", "RemoveHeaders"}, "Sign the helper during embedding.")
    for name, target in targets.items():
        configurations = [objects[key] for key in objects[target["buildConfigurationList"]]["buildConfigurations"]]
        require({item["name"] for item in configurations} == {"Debug", "Release"}, f"{name} needs Debug and Release.")
        for item in configurations:
            settings = item["buildSettings"]
            if name == "BoardcastUpdater":
                require(settings["SKIP_INSTALL"] == "YES", "Only the app should be installed by the archive.")
                require(settings["PRODUCT_BUNDLE_IDENTIFIER"] == "$(BOARDCAST_BUNDLE_IDENTIFIER).updater", "The helper identity must inherit the app identity.")
                require(settings["OTHER_CODE_SIGN_FLAGS"] == "$(inherited) --identifier $(PRODUCT_BUNDLE_IDENTIFIER)", "Preserve release timestamp flags and explicit helper identity.")
                require(settings.get("STRIP_INSTALLED_PRODUCT") == "NO", "Do not strip the already-signed helper product.")
                require(settings.get("DEPLOYMENT_POSTPROCESSING") == "NO", "Skip postprocessing that would re-strip the helper.")
            elif name == "BoardcastTests":
                require(settings["TEST_HOST"] == "$(BUILT_PRODUCTS_DIR)/Boardcast.app/Contents/MacOS/Boardcast", "Tests need the native app host.")
                require(settings["BUNDLE_LOADER"] == "$(TEST_HOST)", "Tests need the app bundle loader.")
                require(settings.get("MACOSX_DEPLOYMENT_TARGET") == "14.0", "XCTest from current Xcode is built for macOS 14.")
            else:
                require(settings["PRODUCT_BUNDLE_IDENTIFIER"] == "$(BOARDCAST_BUNDLE_IDENTIFIER)", "App identity must have one source.")
    resource_phase = next(objects[key] for key in app["buildPhases"] if objects[key]["isa"] == "PBXResourcesBuildPhase")
    resources = [objects[objects[key]["fileRef"]] for key in resource_phase["files"]]
    require(not any(item.get("path") == "Resources" and item.get("lastKnownFileType") == "folder" for item in resources), "Do not copy UI resources as a nested plain directory bundle.")
    for folder in ["Icons", "ThirdParty"]:
        reference = next((item for item in resources if item.get("path") == folder and item.get("lastKnownFileType") == "folder"), None)
        require(reference is not None, f"{folder} must be copied directly to the main bundle resource directory.")
    for filename in ["Localizable.strings", "Localizable.stringsdict", "InfoPlist.strings"]:
        variant = next((item for item in resources if item["isa"] == "PBXVariantGroup" and item.get("name") == filename), None)
        require(variant is not None, f"{filename} must be localized in the main bundle.")
        require({objects[key].get("name") for key in variant["children"]} == {"en", "zh-Hans"}, f"{filename} needs both locales.")
        for key in variant["children"]:
            reference = objects[key]
            locale = reference["name"]
            expected = ROOT / (
                f"Resources/{locale}.lproj/{filename}" if filename == "InfoPlist.strings"
                else f"Sources/Boardcast/Resources/{locale}.lproj/{filename}"
            )
            require(paths[key] == expected, f"{filename} points to the wrong source directory.")
    require(any(item.get("path") == "Assets.xcassets" for item in resources), "The app icon catalog must be compiled.")
    require(not any(item["isa"] == "PBXShellScriptBuildPhase" for item in objects.values()), "A plain local project should not require a generated build phase.")
    require(not any(item["isa"] in ("XCRemoteSwiftPackageReference", "XCSwiftPackageProductDependency") for item in objects.values()), "No remote build dependencies are allowed.")

    scheme = ET.parse(ROOT / "Boardcast.xcodeproj/xcshareddata/xcschemes/Boardcast.xcscheme").getroot()
    require(scheme.attrib.get("LastUpgradeVersion") == "2600", "Scheme should match Xcode 26 recommended settings.")
    for reference in scheme.iter("BuildableReference"):
        require(reference.attrib["BlueprintIdentifier"] in project["targets"], "Scheme references an unknown target.")
    build_entries = scheme.findall("BuildAction/BuildActionEntries/BuildActionEntry")
    require({item.find("BuildableReference").attrib["BlueprintIdentifier"] for item in build_entries} == set(project["targets"]), "The shared scheme must build all three targets.")
    for item in build_entries:
        name = item.find("BuildableReference").attrib["BlueprintName"]
        require(item.attrib["buildForArchiving"] == ("NO" if name == "BoardcastTests" else "YES"), "Archive app and helper, not tests.")
    require(scheme.find("TestAction").attrib["buildConfiguration"] == "Debug", "Tests must use Debug.")
    require(scheme.find("ArchiveAction").attrib["buildConfiguration"] == "Release", "Archive must use Release.")
    require(any(item.attrib.get("key") == "BOARDCAST_TESTING" and item.attrib.get("value") == "1" for item in scheme.iter("EnvironmentVariable")), "Hosted tests need isolated app startup.")
    ET.parse(ROOT / "Boardcast.xcodeproj/project.xcworkspace/contents.xcworkspacedata")

    with (ROOT / "Info.plist").open("rb") as file:
        info = plistlib.load(file)
    for key, expected in {
        "CFBundleIdentifier": "$(PRODUCT_BUNDLE_IDENTIFIER)",
        "CFBundleShortVersionString": "$(MARKETING_VERSION)",
        "CFBundleVersion": "$(CURRENT_PROJECT_VERSION)",
        "LSMinimumSystemVersion": "$(MACOSX_DEPLOYMENT_TARGET)",
    }.items():
        require(info[key] == expected, f"{key} must inherit the Xcode setting.")
    base = (ROOT / "Config/Base.xcconfig").read_text(encoding="utf-8")
    for name in ["BOARDCAST_BUNDLE_IDENTIFIER", "MARKETING_VERSION", "CURRENT_PROJECT_VERSION", "MACOSX_DEPLOYMENT_TARGET", "SWIFT_VERSION"]:
        require(len(re.findall(rf"^{name}\s*=", base, re.MULTILINE)) == 1, f"Expected one {name} definition.")
    require(re.search(r"^MACOSX_DEPLOYMENT_TARGET\s*=\s*13\.0$", base, re.MULTILINE), "The app minimum is macOS 13.")
    require(re.search(r"^SWIFT_VERSION\s*=\s*5\.0$", base, re.MULTILINE), "Compile in Swift 5 language mode.")
    require("ENABLE_HARDENED_RUNTIME = YES" in base and "ENABLE_APP_SANDBOX = NO" in base, "The native app and updater need the same hardened, non-sandbox configuration.")
    require("OTHER_CODE_SIGN_FLAGS = $(inherited) $(BOARDCAST_SIGNING_FLAGS)" in base, "Release must add timestamps without replacing helper identity flags.")
    require("COPY_PHASE_STRIP = NO" in base, "Signed embeds must not be copy-stripped.")
    require("DEAD_CODE_STRIPPING = YES" in base and "STRING_CATALOG_GENERATE_SYMBOLS = YES" in base, "Keep Xcode 26 recommended project settings.")
    require(project["attributes"].get("LastUpgradeCheck") == "2600", "Acknowledge Xcode 26 recommended settings.")
    release = (ROOT / "Config/Release.xcconfig").read_text(encoding="utf-8")
    require("ARCHS = arm64 x86_64" in release and "ONLY_ACTIVE_ARCH = NO" in release, "Release must be universal.")

    icon_root = ROOT / "Resources/Assets.xcassets/AppIcon.appiconset"
    catalog = json.loads((icon_root / "Contents.json").read_text(encoding="utf-8"))
    require(len(catalog["images"]) == 10, "Expected ten macOS icon slots.")
    for item in catalog["images"]:
        pixels = int(item["size"].split("x")[0]) * int(item["scale"][0])
        data = (icon_root / item["filename"]).read_bytes()
        require(data[:8] == b"\x89PNG\r\n\x1a\n", "An app icon is not a PNG.")
        require(struct.unpack(">II", data[16:24]) == (pixels, pixels), f"Incorrect icon size: {item['filename']}.")
    for locale in ["en", "zh-Hans"]:
        for path in [ROOT / f"Sources/Boardcast/Resources/{locale}.lproj/Localizable.strings", ROOT / f"Resources/{locale}.lproj/InfoPlist.strings"]:
            require(path.is_file(), f"Missing localization: {path.relative_to(ROOT)}.")
    package = (ROOT / "Package.swift").read_text(encoding="utf-8")
    require(package.startswith("// swift-tools-version: 5.9") and "swiftLanguageVersions: [.v5]" in package, "SwiftPM must use tools 5.9 and language 5.")
    require('path: "Sources"' in package and 'sources: ["Boardcast", "Shared"]' in package and 'exclude: ["BoardcastUpdater"]' in package, "SwiftPM must compile app and shared policy without another app entry point.")
    for directory in ["Icons", "ThirdParty"]:
        require(f'.copy("Boardcast/Resources/{directory}")' in package, "SwiftPM must preserve resource subdirectories.")
    for locale in ["en", "zh-Hans"]:
        require(f'.process("Boardcast/Resources/{locale}.lproj")' in package, "SwiftPM must include both language resources.")
    for file in (ROOT / "scripts").glob("*.sh"):
        require(b"\r" not in file.read_bytes(), f"Bash script requires LF endings: {file.name}.")
    print(f"PASS: three native targets; app {len(memberships['Boardcast'])} files (including shared policy), helper {len(memberships['BoardcastUpdater'])} files, {len(memberships['BoardcastTests'])} test files; shared scheme, signed helper embedding, main-bundle resources/localizations, ten icons, single product identity.")
    print("Swift compilation, XCTest execution, app runtime, signing and notarization still require Xcode on a Mac.")

if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, IndexError, OSError, ET.ParseError, json.JSONDecodeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
