"""Choose checks from changes since the last successful build, failing open to full checks."""
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


def git(*args):
    return subprocess.check_output(["git", *args]).decode("utf-8")


def without_release_version(path, text):
    # Release numbers do not change dependencies or runtime behavior. All other
    # project/lockfile edits still invalidate the relevant checks.
    if path == "src/Host2VMRelay.csproj":
        return re.sub(r"<Version>[^<]+</Version>", "<Version/>", text)
    if path == "native/Cargo.toml":
        return re.sub(r'(\[workspace\.package\][^\[]*?\bversion\s*=\s*)"[^"]+"', r'\1""', text)
    if path == "native/Cargo.lock":
        blocks = text.split("[[package]]")
        for i, block in enumerate(blocks):
            if re.search(r'^name = "h2vm-(agent|core)"$', block, re.M):
                blocks[i] = re.sub(r'^version = "[^"]+"$', 'version = ""', block, count=1, flags=re.M)
        return "[[package]]".join(blocks)
    return text


def classify(paths, version_changed=False, full=False):
    plan = dict(build=version_changed, ui=False, network=False, native_checks=False,
                packaging=version_changed)
    for path in paths:
        if (path in {"README.md", "CHANGELOG.md", "LICENSE", ".github/workflows/release-notes.yml"}
                or path.startswith(("docs/", "architecture/"))):
            continue
        plan["build"] = True
        if path.startswith("packaging/"):
            plan["packaging"] = True
        elif path.startswith("native/"):
            plan["native_checks"] = plan["network"] = True
        elif path.startswith("src/") and path.endswith(".cs"):
            if not path.startswith(("src/Networking/", "src/Integration/")):
                plan["ui"] = True
            if path.startswith(("src/Networking/", "src/Integration/", "src/Configuration/")) or path == "src/Program.cs":
                plan["network"] = True
        elif path.startswith("src/Assets/"):
            plan["ui"] = True
        else:
            # Shared build files, workflow/test edits and unknown inputs must
            # never silently take the fast path.
            full = True
    if full:
        return dict.fromkeys(plan, True)
    return plan


def api(path):
    request = urllib.request.Request("https://api.github.com/" + path, headers={
        "Authorization": "Bearer " + os.environ["GH_TOKEN"],
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def main():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    repository = os.environ["GITHUB_REPOSITORY"]
    event_name = os.environ["GITHUB_EVENT_NAME"]
    head = git("rev-parse", "HEAD").strip()
    version = ET.parse("src/Host2VMRelay.csproj").findtext("./PropertyGroup/Version")
    full = event_name == "workflow_dispatch"
    paths = []
    version_changed = False
    baseline = None
    try:
        if event_name == "pull_request":
            baseline = git("merge-base", event["pull_request"]["base"]["sha"], head).strip()
        elif not full:
            branch = urllib.parse.quote(os.environ["GITHUB_REF_NAME"], safe="")
            runs = api(f"repos/{repository}/actions/workflows/build.yml/runs?branch={branch}&status=success&per_page=1")
            baseline = runs["workflow_runs"][0]["head_sha"]
            subprocess.run(["git", "merge-base", "--is-ancestor", baseline, head], check=True)
        if baseline:
            changed = git("diff", "--name-only", "--no-renames", "-z", baseline, head).split("\0")
            for path in filter(None, changed):
                if path in {"src/Host2VMRelay.csproj", "native/Cargo.toml", "native/Cargo.lock"}:
                    old = git("show", f"{baseline}:{path}")
                    new = git("show", f"{head}:{path}")
                    if without_release_version(path, old) == without_release_version(path, new):
                        version_changed = True
                        continue
                paths.append(path)
    except (OSError, ValueError, KeyError, IndexError, subprocess.CalledProcessError, urllib.error.URLError) as error:
        print(f"Cannot establish a trusted baseline ({type(error).__name__}); running all checks.")
        full = True
    plan = classify(paths, version_changed, full)
    # Only a missing/draft/prerelease version needs publishing. Do this before
    # expensive packaging, not after producing six archives for an existing tag.
    publish = False
    if event_name == "push" and os.environ["GITHUB_REF"] == "refs/heads/main":
        try:
            release = api(f"repos/{repository}/releases/tags/v{version}")
            publish = release["draft"] or release["prerelease"]
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            publish = True
    plan["publish"] = publish
    if publish:
        plan["build"] = plan["packaging"] = True
    print(json.dumps({"baseline": baseline, "paths": paths, "plan": plan}, indent=2))
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in plan.items():
            output.write(f"{key}={str(value).lower()}\n")
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
        summary.write("## Checks selected for this revision\n\n")
        summary.write(f"Baseline: `{baseline or 'full check'}`\n\n")
        for key, value in plan.items():
            summary.write(f"- {key}: {value}\n")


if __name__ == "__main__":
    main()
