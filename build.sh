#!/usr/bin/env bash
# NameDayMascot (C#) - macOS / Linux 构建脚本
set -e
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "未找到 dotnet，请安装 .NET 10 SDK"
  exit 1
fi

dotnet build -c Release
echo "构建完成，运行："
echo "  dotnet run -c Release"
