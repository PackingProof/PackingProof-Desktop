#!/usr/bin/env bash
#
# 打包 macOS 保存主机：自包含发布 + 组装 .app + 可选签名。
#
# 用法:
#   Tools/Publish-MacHost.sh [输出目录] [RID] [版本号]
# 环境变量:
#   SIGN_IDENTITY  签名身份；不设置时用临时签名（本机自测）
#
# 产物里不包含 config.json、数据库、缓存与日志；运行时数据仍写在用户的
# Application Support 目录下。
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-${repository_root}/package/mac-host}"
runtime_id="${2:-osx-arm64}"
version="${3:-0.0.1}"

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
  <string>PackingProof 保存主机</string>
  <key>CFBundleDisplayName</key>
  <string>PackingProof 保存主机</string>
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
  <!-- 常驻后台服务，不占用 Dock -->
  <key>LSUIElement</key>
  <true/>
</dict>
</plist>
PLIST

echo "==> 签名（未设置 SIGN_IDENTITY 时用临时签名）"
codesign --force --deep --sign "${SIGN_IDENTITY:--}" "${app_bundle}/Contents/MacOS/Host/${host_binary_name}"
codesign --force --deep --sign "${SIGN_IDENTITY:--}" "${app_bundle}"

# 改一下 bundle 时间戳，促使 Finder 刷新图标缓存
touch "${app_bundle}"

echo "==> 校验产物"
if [[ -e "${app_bundle}/Contents/MacOS/Host/config.json" || -e "${app_bundle}/Contents/MacOS/Host/videos.db" || -e "${app_bundle}/Contents/MacOS/Host/log" ]]; then
  echo "产物里不该出现运行数据" >&2
  exit 1
fi
du -sh "${app_bundle}" | sed 's/^/  体积: /'
echo "完成: ${app_bundle}"
