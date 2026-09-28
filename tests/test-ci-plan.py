"""Check that CI can skip unrelated work without missing changed behavior."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("ci_plan", Path(__file__).parents[1] / "scripts/ci-plan.py")
plan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plan)


class ChangeSelectionTests(unittest.TestCase):
    def test_installation_release_does_not_repeat_ui_or_network_checks(self):
        result = plan.classify(["packaging/Host2VMRelay.iss", "docs/RELEASE_NOTES.md"], version_changed=True)
        self.assertEqual(result, dict(build=True, packaging=True, ui=False, network=False, native_checks=False))

    def test_shared_or_unknown_inputs_run_full_checks(self):
        for path in [".github/workflows/build.yml", "scripts/ci-plan.py", "Directory.Build.props", "tests/test-script.cjs", "new-build-input.json"]:
            with self.subTest(path=path):
                self.assertTrue(all(plan.classify([path]).values()))

    def test_transport_and_native_edits_keep_network_coverage(self):
        for path in ["native/relay-core/src/main.rs", "src/Networking/RelaySession.cs", "src/Configuration/Settings.cs", "src/Program.cs"]:
            with self.subTest(path=path):
                self.assertTrue(plan.classify([path])["network"])
        self.assertTrue(plan.classify(["native/Cargo.lock"])["native_checks"])

    def test_docs_only_and_ui_only_have_different_work(self):
        self.assertFalse(any(plan.classify(["docs/DEVELOPMENT.md", "README.md"]).values()))
        ui = plan.classify(["src/UI/MainForm.cs"])
        self.assertTrue(ui["ui"])
        self.assertFalse(ui["network"])
        self.assertFalse(ui["packaging"])

    def test_version_normalization_does_not_hide_dependency_or_build_changes(self):
        lock = '[[package]]\nname = "h2vm-core"\nversion = "0.6.9"\n\n[[package]]\nname = "tokio"\nversion = "1.0.0"\n'
        self.assertEqual(plan.without_release_version("native/Cargo.lock", lock),
                         plan.without_release_version("native/Cargo.lock", lock.replace('0.6.9', '0.6.10')))
        self.assertNotEqual(plan.without_release_version("native/Cargo.lock", lock),
                            plan.without_release_version("native/Cargo.lock", lock.replace('1.0.0', '1.1.0')))
        project = '<Project><Version>0.6.9</Version><TargetFramework>net8.0</TargetFramework></Project>'
        self.assertEqual(plan.without_release_version("src/Host2VMRelay.csproj", project),
                         plan.without_release_version("src/Host2VMRelay.csproj", project.replace('0.6.9', '0.6.10')))
        self.assertNotEqual(plan.without_release_version("src/Host2VMRelay.csproj", project),
                            plan.without_release_version("src/Host2VMRelay.csproj", project.replace('net8.0', 'net10.0')))
        manifest = '[workspace.package]\nversion = "0.6.9"\nedition = "2021"\n[workspace.dependencies]\ntokio = "1.0"\n'
        self.assertEqual(plan.without_release_version("native/Cargo.toml", manifest),
                         plan.without_release_version("native/Cargo.toml", manifest.replace('0.6.9', '0.6.10')))
        self.assertNotEqual(plan.without_release_version("native/Cargo.toml", manifest),
                            plan.without_release_version("native/Cargo.toml", manifest.replace('1.0', '1.1')))

    def test_manual_and_missing_baseline_fallback_is_full(self):
        self.assertTrue(all(plan.classify([], full=True).values()))


if __name__ == "__main__":
    unittest.main()
