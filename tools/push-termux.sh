#!/usr/bin/env bash
# ============================================================
# NameDayMascot —— 在 Termux / Linux 下把源码推送到 GitHub
#
# 用法：
#   sh push-termux.sh                    # 自动在「下载」里找 zip
#   sh push-termux.sh /path/to/xxx.zip   # 指定 zip
#
# 做的事：
#   1. 克隆（或更新）仓库到 ~/NameDayMascot
#   2. 用 zip 里的内容整体覆盖仓库里的项目目录（保留 .git）
#   3. 提交并推送
#
# 注意：这个脚本只负责「推送」。真正编译出 exe 是 GitHub Actions
#       在 windows-latest 上做的，手机不参与构建。
# ============================================================

set -euo pipefail

REPO_OWNER="${REPO_OWNER:-HBrOcean}"
REPO_NAME="${REPO_NAME:-NameDayMascot}"
REPO_URL="${REPO_URL:-https://github.com/$REPO_OWNER/$REPO_NAME.git}"
BRANCH="${BRANCH:-main}"
WORK_DIR="${WORK_DIR:-$HOME/$REPO_NAME}"
MSG="${MSG:-Update NameDayMascot}"

SRC_SUBDIR="NameDayMascot.Avalonia"     # zip 里的顶层目录名

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die() { printf '\n\033[1;31m[!] %s\033[0m\n' "$*" >&2; exit 1; }

# ---------- 0. 前置检查 ----------
command -v git  >/dev/null 2>&1 || die "没装 git。先跑：pkg install git"
command -v unzip >/dev/null 2>&1 || die "没装 unzip。先跑：pkg install unzip"

if ! git config --global user.name >/dev/null 2>&1 || [ -z "$(git config --global user.name)" ]; then
  say "还没设置 git 身份，现在设置"
  printf '输入 GitHub 昵称（例如 HBrOcean）: '
  read -r GIT_NAME
  printf '输入 GitHub 邮箱: '
  read -r GIT_EMAIL
  git config --global user.name  "$GIT_NAME"
  git config --global user.email "$GIT_EMAIL"
fi

# ---------- 1. 找 zip ----------
ZIP="${1:-}"
if [ -z "$ZIP" ]; then
  for p in \
    "$HOME/storage/downloads/NameDayMascot-CSharp-src.zip" \
    "$HOME/storage/download/NameDayMascot-CSharp-src.zip" \
    "$HOME/storage/shared/Download/NameDayMascot-CSharp-src.zip" \
    "$HOME/NameDayMascot-CSharp-src.zip"
  do
    [ -f "$p" ] && { ZIP="$p"; break; }
  done
fi

[ -n "$ZIP" ] && [ -f "$ZIP" ] || die "找不到 NameDayMascot-CSharp-src.zip
    先执行 termux-setup-storage 授权存储，
    再用浏览器把 zip 下载到「下载」目录，或者把路径作为参数传进来：
    sh push-termux.sh /sdcard/Download/NameDayMascot-CSharp-src.zip"

say "使用源码包：$ZIP"

# ---------- 2. 解压到临时目录 ----------
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
unzip -q -o "$ZIP" -d "$TMP"

[ -d "$TMP/$SRC_SUBDIR" ] || die "zip 结构不对：里面没有 $SRC_SUBDIR/ 目录"

# ---------- 3. 克隆或更新仓库 ----------
if [ -d "$WORK_DIR/.git" ]; then
  say "更新已有仓库：$WORK_DIR"
  cd "$WORK_DIR"
  git fetch origin "$BRANCH" || true
else
  say "克隆仓库到：$WORK_DIR"
  git clone "$REPO_URL" "$WORK_DIR"
  cd "$WORK_DIR"
fi

# 对齐远端，避免 diverged
if git rev-parse --verify "origin/$BRANCH" >/dev/null 2>&1; then
  git reset --hard "origin/$BRANCH"
  git clean -fd
fi

# ---------- 4. 判断仓库结构并同步 ----------
# 仓库根就有 .csproj → 扁平结构，源码直接铺在根
if ls ./*.csproj >/dev/null 2>&1; then
  DEST="$(pwd)"
  say "仓库是扁平结构，源码覆盖到仓库根"
else
  DEST="$(pwd)/$SRC_SUBDIR"
  say "仓库是嵌套结构，源码覆盖到 $SRC_SUBDIR/"
fi

mkdir -p "$DEST"

# 清掉目标里除 .git 以外的东西（.git 绝不能动）
find "$DEST" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +

# 复制新内容（zip 里不含 .git，不会覆盖仓库的 .git）
cp -R "$TMP/$SRC_SUBDIR"/. "$DEST"/

say "同步完成，准备提交"
git add -A

if git diff --cached --quiet; then
  say "没有变化，跳过提交"
else
  git commit -m "$MSG"
fi

# ---------- 5. 推送 ----------
say "推送到 origin/$BRANCH"
if ! git push -u origin "$BRANCH"; then
  cat >&2 <<'EOF'

[!] 推送失败，按这个顺序检查：

1) 认证没过？
   现在 Git 不接受账号密码，要用 Personal Access Token 当密码。
   生成：GitHub → Settings → Developer settings → Personal access tokens
        → Tokens (classic) → Generate new token，勾选 repo
   第一次 push 时：
        Username: HBrOcean
        Password: 粘贴 token（不是登录密码）

   想让它记住，先跑一次：
        git config --global credential.helper store

2) 远端有它没有的提交？
        git pull --rebase origin main
        git push -u origin main

3) 分支名不对？
        git branch -M main
        git push -u origin main
EOF
  exit 1
fi

say "推送成功"
printf '\n查看云端构建：https://github.com/%s/%s/actions\n' "$REPO_OWNER" "$REPO_NAME"
printf '想发版的话，打个 tag 再推：\n'
printf '    git tag v0.1.0 && git push origin v0.1.0\n'
