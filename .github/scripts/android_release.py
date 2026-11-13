"""Подготовка Android-релиза: APK загружается только в draft, без замены файлов."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen


STABLE = re.compile(r"v[0-9]+\.[0-9]+\.[0-9]+")
ALPHA = re.compile(r"v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[a-z][a-z0-9]*\.[0-9]+")


class ReleaseError(Exception):
    pass


class GitHub:
    def __init__(self, env):
        self.repo = env["GITHUB_REPOSITORY"]
        self.base = env.get("GITHUB_API_URL", "https://api.github.com")
        self.token = env["GH_TOKEN"]

    def api(self, route, method="GET", data=None, missing_ok=False):
        request = Request(
            f"{self.base}/repos/{self.repo}/{route}",
            data=json.dumps(data).encode() if data is not None else None,
            method=method,
            headers={"Authorization": f"Bearer {self.token}",
                     "Accept": "application/vnd.github+json",
                     "Content-Type": "application/json",
                     "X-GitHub-Api-Version": "2022-11-28"},
        )
        try:
            with urlopen(request, timeout=60) as response:
                return json.load(response)
        except HTTPError as error:
            error.close()
            if missing_ok and error.code == 404:
                return None
            release_write = (method == "POST" and route == "releases") or (
                method == "PATCH" and re.fullmatch(r"releases/[0-9]+", route))
            if release_write and error.code in (403, 404):
                raise ReleaseError(
                    f"GitHub отказал в изменении релиза: HTTP {error.code}, {method} {route}. "
                    "Проверьте доступ. Если коммит меняет .github/workflows относительно основной ветки, "
                    "API требует Workflows:write, которого у GITHUB_TOKEN быть не может. "
                    "Владелец может заранее создать тег и draft на точном SHA, "
                    "повторить CI для загрузки APK и затем сам опубликовать draft. "
                    "Уже загруженный APK сохраняется. Другой вариант — сначала включить изменения "
                    "workflow в основную ветку. Один заранее созданный тег не гарантирует доступ "
                    "к create/update release; дополнительный токен автоматически не используется."
                ) from error
            raise ReleaseError(f"GitHub API: HTTP {error.code}, {method} {route}") from error

    def tag_commit(self, tag):
        ref = self.api(f"git/ref/tags/{quote(tag, safe='')}", missing_ok=True)
        if ref is None:
            return None
        obj = ref["object"]
        # Аннотированные теги тоже должны указывать на проверенный коммит.
        for _ in range(10):
            if obj["type"] == "commit":
                return obj["sha"]
            if obj["type"] != "tag":
                break
            obj = self.api(f"git/tags/{obj['sha']}")["object"]
        raise ReleaseError(f"Тег {tag} не указывает на коммит")

    def release(self, tag):
        published = self.api(f"releases/tags/{quote(tag, safe='')}", missing_ok=True)
        if published is not None:
            return published
        # Endpoint /tags документирован для опубликованных релизов. Черновик ищем
        # в списке с авторизацией; нельзя считать его отсутствующим после одного 404.
        page = 1
        while True:
            releases = self.api(f"releases?per_page=100&page={page}")
            for item in releases:
                if item["tag_name"] == tag:
                    return item
            if len(releases) < 100:
                return None
            page += 1

    def upload(self, tag, apk):
        # shell=False и отдельные аргументы: ни путь, ни тег не исполняются как код.
        subprocess.run(["gh", "release", "upload", tag, str(apk), "--repo", self.repo],
                       check=True)


def release_kind(tag):
    if STABLE.fullmatch(tag):
        return "stable"
    if ALPHA.fullmatch(tag):
        return "alpha"
    raise ReleaseError("Нужен тег vX.Y.Z или vX.Y.Z-alpha.<агент>.<N>")


def request_from_env(env):
    event = env["GITHUB_EVENT_NAME"]
    stable = env.get("RELEASE_INPUT", "").strip()
    alpha = env.get("ALPHA_INPUT", "").strip()
    if stable and alpha:
        raise ReleaseError("release_tag и alpha_tag нельзя задавать одновременно")
    if event == "push" and env["GITHUB_REF"].startswith("refs/tags/"):
        alpha = env["GITHUB_REF"][len("refs/tags/"):]
    if stable and (event != "workflow_dispatch" or not STABLE.fullmatch(stable)):
        raise ReleaseError("Стабильный release_tag vX.Y.Z задаётся только в ручном запуске")
    if alpha and not ALPHA.fullmatch(alpha):
        raise ReleaseError("Альфа-тег должен иметь вид vX.Y.Z-alpha.<агент>.<N>")
    kind = "stable" if stable else "alpha" if alpha else "build"
    expected = env.get("EXPECTED_SHA", "").strip().lower()
    if expected:
        if not re.fullmatch(r"[0-9a-f]{7,40}", expected):
            raise ReleaseError("expected_sha должен содержать 7–40 шестнадцатеричных символов")
        if not env["GITHUB_SHA"].startswith(expected):
            raise ReleaseError("Коммит ветки изменился: GITHUB_SHA не совпадает с expected_sha")
    if kind == "alpha" and event == "workflow_dispatch" and not expected:
        raise ReleaseError("Для ручного запуска альфы укажите expected_sha проверенного коммита ветки")
    if kind == "stable":
        owner = env["GITHUB_REPOSITORY_OWNER"].casefold()
        if any(env.get(key, "").casefold() != owner
               for key in ("GITHUB_ACTOR", "GITHUB_TRIGGERING_ACTOR")):
            raise ReleaseError("Стабильный релиз запускает и перезапускает только владелец репозитория")
        if env["GITHUB_REF"] != "refs/heads/master":
            raise ReleaseError("Стабильный релиз собирается только из master")
        if not expected:
            raise ReleaseError("Для стабильного релиза укажите expected_sha проверенного коммита master")
    return kind, stable or alpha


def asset_name(tag):
    release_kind(tag)
    return f"E2E-{tag[1:]}.apk"


def has_apk(release, tag):
    matches = [asset for asset in release.get("assets", []) if asset["name"] == asset_name(tag)]
    if not matches:
        return False
    if len(matches) != 1 or matches[0].get("state") != "uploaded" or matches[0].get("size", 0) <= 0:
        raise ReleaseError(f"Файл {asset_name(tag)} есть, но загрузка не завершена; файл не заменяем")
    return True


def check_remote(api, tag, sha):
    commit = api.tag_commit(tag)
    if commit is not None and commit != sha:
        raise ReleaseError(f"Тег {tag} уже указывает на другой коммит; используйте новый номер")
    release = api.release(tag)
    if release is not None and release.get("draft"):
        has_any_apk = any(asset.get("name", "").lower().endswith(".apk")
                          for asset in release.get("assets", []))
        if has_any_apk and release.get("target_commitish") != sha:
            raise ReleaseError(f"Черновик {tag} уже содержит APK, но target_commitish не равен "
                               "точному ожидаемому SHA. APK мог быть собран из старого коммита; "
                               "не закрепляем и не используем его повторно. Возьмите новый номер")
    if release is not None and release.get("draft") and commit is None:
        target = release["target_commitish"]
        target_sha = api.api(f"commits/{quote(target, safe='')}")["sha"]
        if target_sha != sha:
            raise ReleaseError(f"Черновик {tag} подготовлен для другого коммита; используйте новый номер")
    return release


def existing_published(release, tag):
    if release is None or release.get("draft"):
        return False
    if not has_apk(release, tag):
        raise ReleaseError(f"Релиз {tag} уже опубликован без {asset_name(tag)}. "
                           "Загрузить APK после публикации нельзя; выпустите новый номер")
    print(f"Релиз {tag} уже содержит APK; опубликованные файлы и тег оставлены без изменений")
    return True


def prepare(env, api):
    kind, tag = request_from_env(env)
    build = True
    if tag:
        release = check_remote(api, tag, env["GITHUB_SHA"])
        if existing_published(release, tag):
            build = False
        elif (kind == "stable" and release is not None and has_apk(release, tag)
              and release.get("target_commitish") == env["GITHUB_SHA"]):
            print(f"Черновик {tag} уже содержит APK и готов к проверке и публикации владельцем")
            build = False
        if not build:
            summary(env, f"{release['html_url']} — APK уже загружен, пересборка и замена не выполнялись.")
    return {"kind": kind, "tag": tag, "build": str(build).lower()}


def attach(env, api):
    kind, tag = request_from_env(env)
    if kind == "build":
        raise ReleaseError("Не задан тег релиза")
    apk = Path(env["APK"])
    if apk.name != asset_name(tag) or not apk.is_file() or apk.stat().st_size == 0:
        raise ReleaseError(f"Ожидался непустой файл {asset_name(tag)}")
    release = check_remote(api, tag, env["GITHUB_SHA"])
    if existing_published(release, tag):
        return
    if release is None:
        data = {"tag_name": tag, "target_commitish": env["GITHUB_SHA"],
                "name": f"E2E {tag[1:]}" + (" (альфа)" if kind == "alpha" else ""),
                "draft": True, "prerelease": kind == "alpha", "make_latest": "false"}
        if kind == "alpha":
            data["body"] = Path(env["NOTES"]).read_text(encoding="utf-8")
        else:
            data["body"] = f"Android APK из master, коммит `{env['GITHUB_SHA']}`."
            data["generate_release_notes"] = True
        release = api.api("releases", "POST", data)
    # Повторный запуск продолжает только черновик. Никогда не удаляем и не заменяем assets.
    if release.get("immutable") or not release.get("draft"):
        raise ReleaseError("Релиз уже опубликован или immutable; загрузка APK остановлена")
    if release.get("target_commitish") != env["GITHUB_SHA"]:
        # Только черновик без APK можно закрепить на SHA: происхождение ранее
        # загруженного APK из символического target_commitish установить нельзя.
        release = api.api(f"releases/{release['id']}", "PATCH",
                          {"target_commitish": env["GITHUB_SHA"]})
    if not has_apk(release, tag):
        api.upload(tag, apk)
    release = check_remote(api, tag, env["GITHUB_SHA"])
    if release is None or not has_apk(release, tag):
        raise ReleaseError("GitHub не подтвердил завершённую загрузку APK; релиз не публикуется")
    if kind == "alpha" and release.get("draft"):
        api.api(f"releases/{release['id']}", "PATCH",
                {"draft": False, "prerelease": True, "make_latest": "false",
                 "target_commitish": env["GITHUB_SHA"]})
        # Публикация через GITHUB_TOKEN не создаёт отдельный release workflow.
        # Проверяем итоговое состояние здесь, не меняя уже опубликованный релиз.
        verify_published({**env, "RELEASE_TAG": tag}, api)
        summary(env, f"Опубликован pre-release: {release['html_url']}")
    elif kind == "stable":
        summary(env, f"Черновик с APK готов: {release['html_url']}\n\n"
                "Владелец проверяет APK и описание, затем нажимает Publish release.")


def verify_published(env, api):
    tag = env["RELEASE_TAG"]
    release_kind(tag)
    release = api.release(tag)
    if release is None or release.get("draft") or not has_apk(release, tag):
        raise ReleaseError(f"В опубликованном {tag} нет готового {asset_name(tag)}. "
                           "Подготовьте новый номер через Run workflow с release_tag или alpha_tag; "
                           "старый релиз и тег не изменяются")
    summary(env, f"В {release['html_url']} уже есть готовый {asset_name(tag)}. "
            "Публикация проверена без пересборки и изменения файлов.")


def summary(env, message):
    print(message)
    if env.get("GITHUB_STEP_SUMMARY"):
        with open(env["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write(message + "\n")


def main():
    env = os.environ
    api = GitHub(env)
    if sys.argv[1] == "prepare":
        outputs = prepare(env, api)
        with open(env["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            for key, value in outputs.items():
                stream.write(f"{key}={value}\n")
    elif sys.argv[1] == "attach":
        attach(env, api)
    elif sys.argv[1] == "verify":
        verify_published(env, api)
    else:
        raise ReleaseError("Неизвестная команда")


if __name__ == "__main__":
    try:
        main()
    except (ReleaseError, OSError, subprocess.CalledProcessError) as error:
        print(f"::error::{error}", file=sys.stderr)
        sys.exit(1)
