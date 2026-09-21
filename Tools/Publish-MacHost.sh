#!/usr/bin/env bash
#
# 打包 macOS 保存主机：自包含发布 + 组装 .app + 可选签名。
#
# 用法:
#   Tools/Publish-MacHost.sh [输出目录] [RID] [版本号]
# 环境变量:
#   SIGN_IDENTITY  Developer ID Application 证书全名；不设置时用临时签名（本机自测）
#   NOTARY_PROFILE 公证凭据在钥匙串里的名字（默认 PackingProofNotary）
#   NOTARIZE=1     签名后提交 Apple 公证并装订（需要 SIGN_IDENTITY 与公证凭据）
#
# 产物里不包含 config.json、数据库、缓存与日志；运行时数据仍写在用户的
# Application Support 目录下。
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-${repository_root}/package/mac-host}"
runtime_id="${2:-osx-arm64}"
# 版本号默认跟随桌面端工程（Mac 包和桌面端同一次发布，版本必须一致）
version="${3:-}"
if [ -z "${version}" ]; then
  version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' \
    "${repository_root}/ExpressPackingMonitoring.Core/ExpressPackingMonitoring.Core.csproj" | head -1)"
  version="${version:-0.0.1}"
fi
notary_profile="${NOTARY_PROFILE:-PackingProofNotary}"
notarize="${NOTARIZE:-0}"

if [[ "${output_root}" == "/" || -z "${output_root}" ]]; then
  echo "输出目录不合法: ${output_root}" >&2
  exit 2
fi

app_name="PackingProofHost"
app_bundle="${output_root}/${app_name}.app"
host_binary_name="ExpressPackingMonitoring.Host"
shell_binary_name="PackingProofHostMenu"

rm -rf "${app_bundle}"
mkdir -p "${app_bundle}/Contents/MacOS/Host" "${app_bundle}/Contents/Resources"

echo "==> 发布 ${runtime_id} 自包含版本"
dotnet publish "${repository_root}/ExpressPackingMonitoring.Host/ExpressPackingMonitoring.Host.csproj" \
  -c Release \
  -r "${runtime_id}" \
  --self-contained true \
  -p:Version="${version}" \
  -p:InformationalVersion="${version}" \
  -o "${app_bundle}/Contents/MacOS/Host"

echo "==> 构建菜单栏壳"
(cd "${repository_root}/MacHostShell" && swift build -c release)
cp "${repository_root}/MacHostShell/.build/release/${shell_binary_name}" \
  "${app_bundle}/Contents/MacOS/${shell_binary_name}"

# 图标一律复用仓库里已有的 app.ico：菜单栏要 36px PNG，应用包要 .icns
app_icon_source="${repository_root}/ExpressPackingMonitoring/app.ico"
sips -s format png -Z 36 "${app_icon_source}" \
  --out "${app_bundle}/Contents/Resources/MenuIcon.png" >/dev/null

icon_work="$(mktemp -d)"
mkdir -p "${icon_work}/AppIcon.iconset"
# Finder/Dock 需要完整的尺寸集合：缺 512 会退回系统默认图标
for spec in "16 16" "32 16" "32 32" "64 32" "128 128" "256 128" "256 256" "512 256" "512 512" "1024 512"; do
  set -- ${spec}
  pixel_size="$1"
  point_size="$2"
  suffix=""
  if [ "${pixel_size}" != "${point_size}" ]; then suffix="@2x"; fi
  sips -s format png -Z "${pixel_size}" "${app_icon_source}" \
    --out "${icon_work}/AppIcon.iconset/icon_${point_size}x${point_size}${suffix}.png" >/dev/null
done
iconutil -c icns "${icon_work}/AppIcon.iconset" -o "${app_bundle}/Contents/Resources/AppIcon.icns"
rm -rf "${icon_work}"

cat > "${app_bundle}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>PackingProof</string>
  <key>CFBundleDisplayName</key>
  <string>PackingProof</string>
  <key>CFBundleIdentifier</key>
  <string>com.packingproof.host</string>
  <key>CFBundleExecutable</key>
  <string>${shell_binary_name}</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${version}</string>
  <key>CFBundleVersion</key>
  <string>${version}</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <!-- 常规窗口应用：有 Dock 图标与主窗口，菜单栏图标仍保留做快捷操作 -->
</dict>
</plist>
PLIST

sign_identity="${SIGN_IDENTITY:--}"
entitlements_file="${repository_root}/Tools/MacHost.entitlements"
host_dir="${app_bundle}/Contents/MacOS/Host"
if [ "${sign_identity}" = "-" ]; then
  # 本机自测：临时签名，用 --deep 一次签完，省时间
  echo "==> 签名（临时签名，仅本机自测；对外分发请设置 SIGN_IDENTITY）"
  codesign --force --deep --sign - "${host_dir}/${host_binary_name}"
  codesign --force --deep --sign - "${app_bundle}"
else
  # 对外分发：Developer ID + 加固运行时。
  # codesign 按"位置"判定嵌套代码——Contents/MacOS 下的每个文件都算，包括 .dll、
  # 网页资源这些非 Mach-O 文件；漏签任何一个，整包签名都会报 not signed at all。
  # --deep 虽能一把签完，但会给嵌套代码写默认 entitlements，所以发布路径逐个签。
  echo "==> 签名（Developer ID + 加固运行时）"
  while IFS= read -r -d '' nested; do
    codesign --force --options runtime --timestamp \
      --sign "${sign_identity}" "${nested}"
  done < <(find "${host_dir}" -type f ! -name "${host_binary_name}" -print0)
  # 自包含 .NET 主机：JIT 与库校验相关 entitlements 只有它需要
  codesign --force --options runtime --timestamp \
    --entitlements "${entitlements_file}" \
    --sign "${sign_identity}" "${host_dir}/${host_binary_name}"
  codesign --force --options runtime --timestamp \
    --sign "${sign_identity}" "${app_bundle}/Contents/MacOS/${shell_binary_name}"
  codesign --force --options runtime --timestamp \
    --sign "${sign_identity}" "${app_bundle}"
fi

# 对外分发用 DMG：整包替换是 macOS 上唯一不破坏签名与公证的方式，
# 所以不做 Windows 那套逐文件增量补丁
echo "==> 生成 DMG"
dmg_path="${output_root}/PackingProof-macOS-${version}.dmg"
# 更新检查靠"文件名里带 macOS 的 .dmg"识别 Mac 包，名字改了更新提示就永远不会出现
dmg_name="$(basename "${dmg_path}")"
case "${dmg_name}" in
  *[Mm][Aa][Cc][Oo][Ss]*.dmg) ;;
  *)
    echo "DMG 名字必须是 *macOS*.dmg（更新检查按这个名字识别平台包）：${dmg_name}" >&2
    exit 1
    ;;
esac
rm -f "${dmg_path}"
dmg_staging="$(mktemp -d)"
cp -R "${app_bundle}" "${dmg_staging}/"
ln -s /Applications "${dmg_staging}/Applications"
hdiutil create -volname "PackingProof ${version}" -srcfolder "${dmg_staging}" \
  -ov -format UDZO "${dmg_path}" >/dev/null
rm -rf "${dmg_staging}"

if [ "${sign_identity}" != "-" ]; then
  codesign --force --timestamp --sign "${sign_identity}" "${dmg_path}"
fi

if [ "${notarize}" = "1" ] && [ "${sign_identity}" != "-" ]; then
  echo "==> 公证 DMG（keychain-profile: ${notary_profile}）"
  xcrun notarytool submit "${dmg_path}" --keychain-profile "${notary_profile}" --wait
  # DMG 与里面的 .app 都装订票据，离线首次启动也能过 Gatekeeper
  xcrun stapler staple "${dmg_path}"
  xcrun stapler staple "${app_bundle}"
  echo "已公证并装订"
fi

# 改一下 bundle 时间戳，促使 Finder 刷新图标缓存
touch "${app_bundle}"

echo "==> 校验产物"
if [[ -e "${app_bundle}/Contents/MacOS/Host/config.json" || -e "${app_bundle}/Contents/MacOS/Host/videos.db" || -e "${app_bundle}/Contents/MacOS/Host/log" ]]; then
  echo "产物里不该出现运行数据" >&2
  exit 1
fi
du -sh "${app_bundle}" | sed 's/^/  体积: /'
echo "完成: ${app_bundle}"
