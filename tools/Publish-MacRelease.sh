#!/usr/bin/env bash
#
# 把 macOS 分发包（DMG）挂到对应版本的 Release 上。
#
# 用法:
#   tools/Publish-MacRelease.sh [版本号] [github|gitee|both]
# 环境变量:
#   SIGN_IDENTITY / NOTARY_PROFILE / NOTARIZE   传给 Publish-MacHost.sh 做签名与公证
#   SIGN_KEYCHAIN / SIGN_KEYCHAIN_PASSWORD      签名专用钥匙串（无人值守/SSH 发布时必须）
#   GITEE_TOKEN                                 优先取环境变量，其次读仓库根 .env
#
# 为什么单独一个脚本：Mac 包只能在 Mac 上构建（swift + codesign），Windows 侧的
# 发布脚本够不到；而更新检查判断"这个版本有没有 Mac 包"就是看该 Release 里
# 有没有 DMG，所以必须有人把它挂到同一个 vX.Y.Z Release 上（不另开 mac 标签）。
#
# 脚本会顺带把 update_v<版本>.json 里的 platforms.macos.package 补上（只作声明与
# 下载入口，判定仍以资产为准；清单与资产不一致时以资产为准）。
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repository_root}"

# 仓库根 .env 里放本机凭据（Gitee 令牌、签名钥匙串密码），不提交：
# 先加载它，SSH 会话里也能拿到签名钥匙串密码，否则登录钥匙串锁定就签不了名。
if [ -f "${repository_root}/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "${repository_root}/.env"
  set +a
fi

brand_profile="${BRAND_PROFILE:-packingproof}"
brand_profile_dir="${BRAND_PROFILE_DIR:-}"
if [[ -z "${brand_profile_dir}" ]]; then
  brand_external_root="${BRAND_PROFILE_ROOT:-}"
  local_profiles_props="${repository_root}/branding/profiles.local.props"
  if [[ -z "${brand_external_root}" && -f "${local_profiles_props}" ]]; then
    brand_external_root="$(sed -n 's:.*<BrandExternalProfilesRoot>\(.*\)</BrandExternalProfilesRoot>.*:\1:p' "${local_profiles_props}" | head -1)"
  fi
  if [[ -n "${brand_external_root}" ]]; then
    brand_profile_dir="${brand_external_root}/${brand_profile}"
  else
    brand_profile_dir="${repository_root}/branding/profiles/${brand_profile}"
  fi
fi
brand_props="${brand_profile_dir}/Brand.props"
if [[ ! -f "${brand_props}" ]]; then
  echo "找不到定制档案: ${brand_profile} (${brand_props})" >&2
  exit 2
fi
brand_value() {
  sed -n "s:.*<$1>\\(.*\\)</$1>.*:\\1:p" "${brand_props}" | head -1
}
brand_artifact_prefix="$(brand_value BrandArtifactPrefix)"
brand_repository_slug="$(brand_value BrandRepositorySlug)"
if [[ -z "${brand_artifact_prefix}" || -z "${brand_repository_slug}" ]]; then
  echo "软件配置缺少 Mac 发布字段: ${brand_profile}" >&2
  exit 2
fi

version="${1:-}"
if [[ -z "${version}" ]]; then
  version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' \
    "${repository_root}/Directory.Build.props" | head -1)"
  version="${version:-}"
fi
if [[ -z "${version}" ]]; then
  echo "用法: tools/Publish-MacRelease.sh [版本号] [github|gitee|both]" >&2
  exit 2
fi

target="${2:-github}"
case "${target}" in
  github | gitee | both) ;;
  *)
    echo "目标只能是 github、gitee 或 both" >&2
    exit 2
    ;;
esac

tag="v${version}"
github_repo="${brand_repository_slug}"
dmg_path="${repository_root}/package/mac-host/${brand_artifact_prefix}-macOS-${version}.dmg"

# 只发布已在主干、且标签就指向当前提交的版本：先构建再打标签会让身份对不上
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "工作区有未提交改动，先提交并合并到主干再发布" >&2
  exit 2
fi
if ! git rev-parse -q --verify "refs/tags/${tag}" >/dev/null; then
  echo "找不到标签 ${tag}；发布必须打在已合并到主干的提交上" >&2
  exit 2
fi
if [[ "$(git rev-parse "${tag}^{commit}")" != "$(git rev-parse HEAD)" ]]; then
  echo "标签 ${tag} 不指向当前提交（$(git rev-parse --short HEAD)），请先同步主干" >&2
  exit 2
fi

echo "==> 构建 macOS 包（${tag}）"
BRAND_PROFILE="${brand_profile}" BRAND_PROFILE_DIR="${brand_profile_dir}" "${repository_root}/tools/Publish-MacHost.sh" \
  "${repository_root}/package/mac-host" "osx-arm64" "${version}"
if [[ ! -f "${dmg_path}" ]]; then
  echo "没有产出 ${dmg_path}" >&2
  exit 1
fi

if [[ "${target}" == "github" || "${target}" == "both" ]]; then
  echo "==> 上传 DMG 到 GitHub Release ${tag}"
  gh release upload "${tag}" "${dmg_path}" --repo "${github_repo}" --clobber
fi

if [[ "${target}" == "gitee" || "${target}" == "both" ]]; then
  gitee_bot_token_file="${HOME}/.config/PackingProof/gitee-release-bot/token.txt"
  if [[ ! -f "${gitee_bot_token_file}" ]]; then
    echo "缺少 PackingProof-Release-Bot token 文件：${gitee_bot_token_file}" >&2
    exit 2
  fi
  GITEE_TOKEN="$(tr -d '\r\n' < "${gitee_bot_token_file}")"
  if [[ -z "${GITEE_TOKEN}" ]]; then
    echo "PackingProof-Release-Bot token 文件为空" >&2
    exit 2
  fi
  export GITEE_TOKEN
  echo "==> 上传 DMG 到 Gitee Release ${tag}"
  gitee release upload --repo "${github_repo}" "${tag}" "${dmg_path}"
fi

# 清单里补一条平台声明：只作下载入口与人工核对，判定仍以资产是否存在为准
echo "==> 更新 update_v${version}.json 的平台声明"
work_dir="$(mktemp -d)"
trap 'rm -rf "${work_dir}"' EXIT
if [[ "${target}" == "github" || "${target}" == "both" ]]; then
  if gh release download "${tag}" --repo "${github_repo}" \
      --pattern "update_v${version}.json" --dir "${work_dir}" --clobber 2>/dev/null; then
python3 - "${work_dir}/update_v${version}.json" "${version}" "${brand_artifact_prefix}" <<'PY'
import json, sys
path, version, artifact_prefix = sys.argv[1], sys.argv[2], sys.argv[3]
with open(path, encoding='utf-8') as handle:
    data = json.load(handle)
data.setdefault('platforms', {})['macos'] = {
    'version': version,
    'package': f'{artifact_prefix}-macOS-{version}.dmg',
}
with open(path, 'w', encoding='utf-8') as handle:
    json.dump(data, handle, ensure_ascii=False, indent=2)
    handle.write('\n')
PY
    gh release upload "${tag}" "${work_dir}/update_v${version}.json" \
      --repo "${github_repo}" --clobber
  else
    echo "（Release 里没有 update_v${version}.json，跳过平台声明；不影响更新判定）"
  fi
fi

echo "==> 自检：Release 里的 Mac 包"
if [[ "${target}" == "github" || "${target}" == "both" ]]; then
  gh release view "${tag}" --repo "${github_repo}" --json assets \
    --jq ".assets[].name" | grep -i 'macos.*\.dmg' | sed 's/^/  ✓ /' \
    || { echo "GitHub Release 上没找到 macOS 包，更新检查会跳过这个版本" >&2; exit 1; }
fi

echo "完成：${tag} 已带上 macOS 包 ${dmg_path}"
