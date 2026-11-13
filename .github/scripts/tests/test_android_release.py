"""Проверки сценариев релиза без сети, GitHub и настоящих APK."""

from copy import deepcopy
from contextlib import redirect_stdout
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError


SPEC = importlib.util.spec_from_file_location(
    "android_release", Path(__file__).resolve().parents[1] / "android_release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)
SHA = "1234567" + "a" * 33
OTHER_SHA = "b" * 40


def environment(**changes):
    env = {"GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF": "refs/heads/master",
           "GITHUB_SHA": SHA, "GITHUB_ACTOR": "Owner", "GITHUB_TRIGGERING_ACTOR": "Owner",
           "GITHUB_REPOSITORY_OWNER": "Owner", "GITHUB_REPOSITORY": "Owner/Project",
           "GH_TOKEN": "offline-test-token", "EXPECTED_SHA": SHA,
           "RELEASE_INPUT": "v1.2.3"}
    env.update(changes)
    return env


def asset(tag="v1.2.3", **changes):
    result = {"name": release.asset_name(tag), "state": "uploaded", "size": 10}
    result.update(changes)
    return result


def draft(**changes):
    result = {"id": 1, "draft": True, "immutable": False, "target_commitish": SHA,
              "html_url": "https://github.com/Owner/Project/releases/tag/v1.2.3", "assets": []}
    result.update(changes)
    return result


class FakeGitHub:
    def __init__(self, current=None, commit=None):
        self.current = deepcopy(current)
        self.commit = commit
        self.target_commit = SHA
        self.mutations = []

    def tag_commit(self, tag):
        return self.commit

    def release(self, tag):
        return deepcopy(self.current)

    def api(self, route, method="GET", data=None):
        if method == "GET" and route.startswith("commits/"):
            return {"sha": self.target_commit}
        self.mutations.append((method, route, data))
        if method == "POST" and route == "releases":
            self.current = draft(**data)
        elif method == "PATCH" and route == "releases/1":
            self.current.update(data)
        else:
            raise AssertionError((method, route))
        return deepcopy(self.current)

    def upload(self, tag, apk):
        if not self.current["draft"]:
            raise AssertionError("Попытка загрузки в опубликованный релиз")
        self.mutations.append(("UPLOAD", tag, str(apk)))
        self.current["assets"].append(asset(tag))


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(redirect_stdout(io.StringIO()))
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)

    def attach(self, api, **changes):
        env = environment(**changes)
        _, tag = release.request_from_env(env)
        apk = Path(self.temp.name) / release.asset_name(tag)
        apk.write_bytes(b"offline test placeholder")
        notes = Path(self.temp.name) / "notes.md"
        notes.write_text("Изменения альфы", encoding="utf-8")
        env.update(APK=str(apk), NOTES=str(notes))
        release.attach(env, api)

    def test_regular_build_does_not_read_github(self):
        self.assertEqual(release.prepare(environment(RELEASE_INPUT=""), None),
                         {"kind": "build", "tag": "", "build": "true"})

    def test_stable_requires_owner_master_and_expected_commit(self):
        for changes in [{"GITHUB_ACTOR": "Contributor"}, {"GITHUB_TRIGGERING_ACTOR": "Contributor"},
                        {"GITHUB_REF": "refs/heads/codex/dev"}, {"EXPECTED_SHA": ""},
                        {"EXPECTED_SHA": OTHER_SHA}, {"EXPECTED_SHA": "$(echo injected)"}]:
            with self.subTest(changes=changes), self.assertRaises(release.ReleaseError):
                release.prepare(environment(**changes), None)

    def test_conflicting_or_invalid_tags_fail_before_api(self):
        for changes in [{"ALPHA_INPUT": "v1.2.3-alpha.codex.1"},
                        {"RELEASE_INPUT": "v1.2.3;echo bad"}, {"RELEASE_INPUT": "v1.2.3\nwrong"},
                        {"GITHUB_EVENT_NAME": "push"}]:
            with self.subTest(changes=changes), self.assertRaises(release.ReleaseError):
                release.prepare(environment(**changes), None)

    def test_short_expected_sha_and_case_insensitive_owner_are_accepted(self):
        result = release.prepare(environment(EXPECTED_SHA=SHA[:7], GITHUB_ACTOR="owner"), FakeGitHub())
        self.assertEqual(result, {"kind": "stable", "tag": "v1.2.3", "build": "true"})

    def test_alpha_tag_push_and_dispatch_work_from_development_branch(self):
        tag = "v1.2.4-alpha.codex.1"
        for changes in [{"GITHUB_EVENT_NAME": "push", "GITHUB_REF": f"refs/tags/{tag}"},
                        {"GITHUB_REF": "refs/heads/codex/dev", "ALPHA_INPUT": tag}]:
            with self.subTest(changes=changes):
                result = release.prepare(environment(RELEASE_INPUT="", **changes), FakeGitHub())
                self.assertEqual(result, {"kind": "alpha", "tag": tag, "build": "true"})

    def test_manual_alpha_requires_expected_sha_but_tag_push_does_not(self):
        tag = "v1.2.4-alpha.codex.1"
        with self.assertRaisesRegex(release.ReleaseError, "expected_sha"):
            release.prepare(environment(RELEASE_INPUT="", ALPHA_INPUT=tag, EXPECTED_SHA=""), None)
        result = release.prepare(environment(RELEASE_INPUT="", EXPECTED_SHA="",
                                             GITHUB_EVENT_NAME="push", GITHUB_REF=f"refs/tags/{tag}"),
                                 FakeGitHub(commit=SHA))
        self.assertEqual(result, {"kind": "alpha", "tag": tag, "build": "true"})

    def test_existing_tag_on_other_commit_is_not_moved(self):
        api = FakeGitHub(commit=OTHER_SHA)
        with self.assertRaises(release.ReleaseError):
            release.prepare(environment(), api)
        self.assertEqual(api.mutations, [])

    def test_draft_without_tag_must_target_same_commit(self):
        api = FakeGitHub(draft())
        api.target_commit = OTHER_SHA
        with self.assertRaises(release.ReleaseError):
            release.prepare(environment(), api)
        self.assertEqual(api.mutations, [])

    def test_published_release_with_apk_is_no_op_for_stable_and_alpha(self):
        for tag in ["v1.2.3", "v1.2.4-alpha.codex.1"]:
            changes = {"RELEASE_INPUT": tag} if "alpha" not in tag else {
                "RELEASE_INPUT": "", "ALPHA_INPUT": tag}
            with self.subTest(tag=tag):
                api = FakeGitHub(draft(draft=False, immutable=True, assets=[asset(tag)]), SHA)
                self.assertEqual(release.prepare(environment(**changes), api)["build"], "false")
                self.attach(api, **changes)
                self.assertEqual(api.mutations, [])

    def test_published_release_without_apk_is_not_repaired(self):
        api = FakeGitHub(draft(draft=False, immutable=True), SHA)
        with self.assertRaisesRegex(release.ReleaseError, "новый номер"):
            self.attach(api)
        self.assertEqual(api.mutations, [])

    def test_new_stable_stays_draft_with_asset_and_exact_commit(self):
        api = FakeGitHub()
        self.attach(api)
        self.assertEqual([x[0] for x in api.mutations], ["POST", "UPLOAD"])
        self.assertEqual(api.current["target_commitish"], SHA)
        self.assertTrue(api.current["draft"])
        self.assertFalse(api.current["prerelease"])
        self.assertTrue(release.has_apk(api.current, "v1.2.3"))

    def test_new_alpha_uploads_before_publishing(self):
        api = FakeGitHub()
        self.attach(api, RELEASE_INPUT="", ALPHA_INPUT="v1.2.4-alpha.codex.1")
        self.assertEqual([x[0] for x in api.mutations], ["POST", "UPLOAD", "PATCH"])
        self.assertTrue(api.mutations[0][2]["draft"])
        self.assertFalse(api.current["draft"])
        self.assertTrue(api.current["prerelease"])
        self.assertEqual(api.current["make_latest"], "false")

    def test_existing_draft_asset_is_preserved(self):
        api = FakeGitHub(draft(assets=[asset()]))
        self.assertEqual(release.prepare(environment(), api)["build"], "false")
        self.attach(api)
        self.assertEqual(api.mutations, [])

    def test_alpha_checks_publication_state_after_patch(self):
        class StillDraft(FakeGitHub):
            def api(self, route, method="GET", data=None):
                result = super().api(route, method, data)
                if method == "PATCH" and data.get("draft") is False:
                    self.current["draft"] = True
                return result

        api = StillDraft()
        with self.assertRaises(release.ReleaseError):
            self.attach(api, RELEASE_INPUT="", ALPHA_INPUT="v1.2.4-alpha.codex.1")
        self.assertEqual([entry[0] for entry in api.mutations], ["POST", "UPLOAD", "PATCH"])

    def test_alpha_checks_uploaded_apk_after_publication(self):
        class MissingPublishedAsset(FakeGitHub):
            def api(self, route, method="GET", data=None):
                result = super().api(route, method, data)
                if method == "PATCH" and data.get("draft") is False:
                    self.current["assets"] = []
                return result

        api = MissingPublishedAsset()
        with self.assertRaises(release.ReleaseError):
            self.attach(api, RELEASE_INPUT="", ALPHA_INPUT="v1.2.4-alpha.codex.1")
        self.assertEqual([entry[0] for entry in api.mutations], ["POST", "UPLOAD", "PATCH"])

    def test_existing_alpha_draft_resumes_without_replacing_asset(self):
        tag = "v1.2.4-alpha.codex.1"
        api = FakeGitHub(draft(assets=[asset(tag)]))
        self.attach(api, RELEASE_INPUT="", ALPHA_INPUT=tag)
        self.assertEqual([x[0] for x in api.mutations], ["PATCH"])

    def test_empty_draft_target_branch_is_pinned_before_upload(self):
        api = FakeGitHub(draft(target_commitish="master"))
        self.assertEqual(release.prepare(environment(), api)["build"], "true")
        self.attach(api)
        self.assertEqual([entry[0] for entry in api.mutations], ["PATCH", "UPLOAD"])
        self.assertEqual(api.mutations[0], ("PATCH", "releases/1", {"target_commitish": SHA}))
        self.assertTrue(api.current["draft"])

    def test_draft_apk_requires_literal_expected_sha_even_with_matching_tag(self):
        for tag in ["v1.2.3", "v1.2.4-alpha.codex.1"]:
            changes = {"RELEASE_INPUT": tag} if "alpha" not in tag else {
                "RELEASE_INPUT": "", "ALPHA_INPUT": tag}
            for target in ["master", "codex/dev", OTHER_SHA]:
                for commit in [None, SHA]:
                    with self.subTest(tag=tag, target=target, commit=commit):
                        api = FakeGitHub(draft(target_commitish=target, assets=[asset(tag)]), commit)
                        with self.assertRaisesRegex(release.ReleaseError, "APK.*SHA"):
                            release.prepare(environment(**changes), api)
                        with self.assertRaisesRegex(release.ReleaseError, "APK.*SHA"):
                            self.attach(api, **changes)
                        self.assertEqual(api.mutations, [])

    def test_incomplete_asset_is_not_overwritten(self):
        for changes in [{"size": 0}, {"state": "starter"}]:
            with self.subTest(changes=changes):
                api = FakeGitHub(draft(assets=[asset(**changes)]))
                with self.assertRaises(release.ReleaseError):
                    self.attach(api)
                self.assertEqual(api.mutations, [])

    def test_upload_failure_leaves_draft_unpublished(self):
        api = FakeGitHub()
        with patch.object(api, "upload", side_effect=OSError("upload failed")):
            with self.assertRaises(OSError):
                self.attach(api, RELEASE_INPUT="", ALPHA_INPUT="v1.2.4-alpha.codex.1")
        self.assertTrue(api.current["draft"])
        self.assertEqual([x[0] for x in api.mutations], ["POST"])

    def test_upload_must_be_confirmed_before_publish(self):
        api = FakeGitHub()
        with patch.object(api, "upload"):
            with self.assertRaises(release.ReleaseError):
                self.attach(api, RELEASE_INPUT="", ALPHA_INPUT="v1.2.4-alpha.codex.1")
        self.assertTrue(api.current["draft"])
        self.assertEqual([x[0] for x in api.mutations], ["POST"])

    def test_published_event_only_checks_existing_asset(self):
        for apk_present in [True, False]:
            with self.subTest(apk_present=apk_present):
                api = FakeGitHub(draft(draft=False, immutable=True,
                                      assets=[asset()] if apk_present else []))
                env = environment(RELEASE_TAG="v1.2.3")
                if apk_present:
                    release.verify_published(env, api)
                else:
                    with self.assertRaises(release.ReleaseError):
                        release.verify_published(env, api)
                self.assertEqual(api.mutations, [])

    def test_only_404_means_missing_not_auth_or_server_error(self):
        api = release.GitHub(environment())
        for status in [404, 403, 500]:
            error = HTTPError("https://api.github.com/test", status, "test", {}, io.BytesIO())
            with self.subTest(status=status), patch.object(release, "urlopen", side_effect=error):
                if status == 404:
                    self.assertIsNone(api.api("releases/tags/v1.2.3", missing_ok=True))
                else:
                    with self.assertRaises(release.ReleaseError):
                        api.api("releases/tags/v1.2.3", missing_ok=True)

    def test_release_write_permission_error_explains_owner_action(self):
        api = release.GitHub(environment())
        for method, route in [("POST", "releases"), ("PATCH", "releases/1")]:
            for status in [403, 404]:
                error = HTTPError("https://api.github.com/test", status, "test", {}, io.BytesIO())
                with self.subTest(method=method, status=status):
                    with patch.object(release, "urlopen", side_effect=error):
                        with self.assertRaisesRegex(release.ReleaseError, "GITHUB_TOKEN.*Владелец"):
                            api.api(route, method, {"target_commitish": SHA})

    def test_draft_lookup_after_tag_endpoint_404_follows_pagination(self):
        api = release.GitHub(environment())
        wanted = draft(tag_name="v1.2.3")
        first_page = [draft(tag_name=f"v0.0.{i}") for i in range(100)]
        with patch.object(api, "api", side_effect=[None, first_page, [wanted]]) as request:
            self.assertEqual(api.release("v1.2.3"), wanted)
            self.assertEqual(request.call_args.args, ("releases?per_page=100&page=2",))

    def test_annotated_tag_resolves_to_commit(self):
        api = release.GitHub(environment())
        with patch.object(api, "api", side_effect=[{"object": {"type": "tag", "sha": "tag-object"}},
                                                 {"object": {"type": "commit", "sha": SHA}}]):
            self.assertEqual(api.tag_commit("v1.2.3"), SHA)


if __name__ == "__main__":
    unittest.main()
