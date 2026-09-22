#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
用法: windows-vms.sh {start|stop|restart|status} {dev|clean|all}

  dev    win11-dev
  clean  win11-clean
  all    两台虚拟机（按 dev、clean 顺序执行）
EOF
}

if [[ ${1:-} == --help || ${1:-} == -h ]]; then
  usage
  exit 0
fi

if (( $# != 2 )); then
  usage >&2
  exit 2
fi

action=$1
target=$2
case "$action" in
  start|stop|restart|status) ;;
  *) usage >&2; exit 2 ;;
esac

case "$target" in
  dev) vms=(win11-dev) ;;
  clean) vms=(win11-clean) ;;
  all) vms=(win11-dev win11-clean) ;;
  *) usage >&2; exit 2 ;;
esac

for vm in "${vms[@]}"; do
  controller="/data/vm/$vm/config/vm.py"
  if [[ ! -f $controller ]]; then
    echo "找不到虚拟机控制脚本: $controller" >&2
    exit 1
  fi
done

run_action() {
  local command=$1 vm
  for vm in "${vms[@]}"; do
    echo "[$vm] $command"
    python3 "/data/vm/$vm/config/vm.py" "$command"
  done
}

if [[ $action == restart ]]; then
  # 全部正常关机完成后才开始启动，避免对仍在运行的磁盘再次启动 QEMU。
  run_action stop
  run_action start
else
  run_action "$action"
fi
