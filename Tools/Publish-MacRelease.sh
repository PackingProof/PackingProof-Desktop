#!/usr/bin/env bash
#
# 把 macOS 分发包（DMG）挂到对应版本的 Release 上。
#
# 用法:
#   Tools/Publish-MacRelease.sh [版本号] [github|gitee|both]
# 环境变量:
#   SIGN_IDENTITY / NOTARY_PROFILE / NOTARIZE   传给 Publish-MacHost.sh 做签名与公证
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

version="${1:-}"
if [[ -z "${version}" ]]; then
  version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' \
    "${repository_root}/ExpressPackingMonitoring.Core/ExpressPackingMonitoring.Core.csproj" | head -1)"
  version="${version:-}"
fi
if [[ -z "${version}" ]]; then
  echo "用法: Tools/Publish-MacRelease.sh [版本号] [github|gitee|both]" >&2
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
github_repo="PackingProof/PackingProof-Desktop"
dmg_path="${repository_root}/package/mac-host/PackingProof-macOS-${version}.dmg"

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
"${repository_root}/Tools/Publish-MacHost.sh" \
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
  if [[ -f "${repository_root}/.env" ]]; then
    set -a
    # shellcheck disable=SC1091
    . "${repository_root}/.env"
    set +a
  fi
  if [[ -z "${GITEE_TOKEN:-}" ]]; then
    echo "缺少 GITEE_TOKEN（环境变量或仓库根 .env），跳过 Gitee 上传" >&2
  else
    echo "==> 上传 DMG 到 Gitee Release ${tag}"
    gitee release upload --repo "${github_repo}" "${tag}" "${dmg_path}"
  fi
fi

# 清单里补一条平台声明：只作下载入口与人工核对，判定仍以资产是否存在为准
echo "==> 更新 update_v${version}.json 的平台声明"
work_dir="$(mktemp -d)"
trap 'rm -rf "${work_dir}"' EXIT
if [[ "${target}" == "github" || "${target}" == "both" ]]; then
  if gh release download "${tag}" --repo "${github_repo}" \
      --pattern "update_v${version}.json" --dir "${work_dir}" --clobber 2>/dev/null; then
    python3 - "${work_dir}/update_v${version}.json" "${version}" <<'PY'
import json, sys
path, version = sys.argv[1], sys.argv[2]
with open(path, encoding='utf-8') as handle:
    data = json.load(handle)
data.setdefault('platforms', {})['macos'] = {
    'version': version,
    'package': f'PackingProof-macOS-{version}.dmg',
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
