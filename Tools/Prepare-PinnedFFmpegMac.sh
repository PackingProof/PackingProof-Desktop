#!/usr/bin/env bash
#
# 下载并校验 macOS 版 FFmpeg。
#
# 与 Windows 侧 Prepare-PinnedFFmpeg.ps1 同一套做法：来源与 SHA256 锁在
# Tools/ffmpeg-macos-baseline.json 里，二进制不进仓库，只放进本地依赖缓存
# package/dependency-cache/ffmpeg-macos/<版本>/，打包时再拷进 .app。
#
# 用法: Tools/Prepare-PinnedFFmpegMac.sh
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
baseline="${repository_root}/Tools/ffmpeg-macos-baseline.json"

if [[ ! -f "${baseline}" ]]; then
  echo "找不到基线文件：${baseline}" >&2
  exit 2
fi

baseline_fields="$(python3 - "${baseline}" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as handle:
    data = json.load(handle)
package = data.get('package', {})
urls = package.get('urls') or []
print('\t'.join([
    str(data.get('version', '')),
    str(package.get('size', 0)),
    str(package.get('sha256', '')),
    str(package.get('entry', '')),
    str(package.get('executable_size', 0)),
    str(package.get('executable_sha256', '')),
]))
for url in urls:
    print(url)
PY
)"

IFS=$'\t' read -r version size sha256 entry executable_size executable_sha256 \
  <<<"$(head -1 <<<"${baseline_fields}")"
urls=()
while IFS= read -r line; do
  [[ -n "${line}" ]] && urls+=("${line}")
done < <(tail -n +2 <<<"${baseline_fields}")

if [[ -z "${version}" || ${#urls[@]} -eq 0 ]]; then
  echo "基线里缺少版本号或下载地址" >&2
  exit 2
fi
if [[ -z "${sha256}" ]]; then
  echo "基线里没有锁 SHA256：先把校验和填进 ${baseline} 再准备 FFmpeg" >&2
  exit 2
fi
if [[ -z "${entry}" || -z "${executable_sha256}" ]]; then
  echo "基线里缺少包内路径或可执行文件校验和：先把字段补齐再准备 FFmpeg" >&2
  exit 2
fi

cache_dir="${repository_root}/package/dependency-cache/ffmpeg-macos/${version}"
target="${cache_dir}/ffmpeg"
mkdir -p "${cache_dir}"

sha256_of() {
  shasum -a 256 "$1" | cut -d' ' -f1
}

size_of() {
  stat -f%z "$1"
}

# 校验对象是最终要打进包里的可执行文件，不是下载到的压缩包
verify_executable() {
  local file="$1" actual_sha actual_size
  [[ -f "${file}" ]] || return 1
  actual_sha="$(sha256_of "${file}")"
  actual_size="$(size_of "${file}")"
  [[ "${actual_sha}" == "${executable_sha256}" ]] || return 1
  [[ "${executable_size}" == "0" || "${actual_size}" == "${executable_size}" ]] || return 1
  return 0
}

if verify_executable "${target}"; then
  echo "已就绪：${target}（$(size_of "${target}") 字节）"
else
  echo "==> 下载 macOS FFmpeg ${version}"
  archive="${cache_dir}/$(basename "${urls[0]}")"
  archive_tmp="${archive}.download"
  downloaded=0
  for url in "${urls[@]}"; do
    echo "    来源: ${url}"
    if curl -L --retry 5 --retry-delay 3 --max-time 1800 -C - -o "${archive_tmp}" "${url}"; then
      actual_sha="$(sha256_of "${archive_tmp}")"
      if [[ "${actual_sha}" != "${sha256}" ]]; then
        echo "  SHA256 与基线不一致，换下一个来源" >&2
        continue
      fi
      if [[ "${size}" != "0" && "$(size_of "${archive_tmp}")" != "${size}" ]]; then
        echo "  压缩包体积与基线不一致（基线 ${size}），换下一个来源" >&2
        continue
      fi
      downloaded=1
      break
    fi
    echo "  下载失败，换下一个来源" >&2
  done
  if [[ "${downloaded}" != "1" ]]; then
    echo "所有来源都没能拿到与基线一致的文件" >&2
    exit 1
  fi
  mv -f "${archive_tmp}" "${archive}"

  echo "==> 解出可执行文件 ${entry}"
  unzip -p "${archive}" "${entry}" > "${target}.download"
  if ! verify_executable "${target}.download"; then
    echo "包内可执行文件与基线不一致" >&2
    echo "  实际: $(sha256_of "${target}.download") / $(size_of "${target}.download") 字节" >&2
    echo "  基线: ${executable_sha256} / ${executable_size} 字节" >&2
    exit 1
  fi
  chmod +x "${target}.download"
  mv -f "${target}.download" "${target}"
  echo "完成：${target}（$(size_of "${target}") 字节）"
fi

# 稳定的取用路径：打包脚本不关心具体版本目录
ln -sfn "${cache_dir}" "${repository_root}/package/dependency-cache/ffmpeg-macos/current"
echo "打包脚本从 package/dependency-cache/ffmpeg-macos/current/ffmpeg 取用"
