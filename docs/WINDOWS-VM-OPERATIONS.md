# Windows 虚拟机运行与维护手册

基线日期：2026-09-21（America/Toronto）。本手册记录**已经部署**的两台通用 Windows 虚拟机，供后续项目开发、测试、维护和故障恢复使用。首次使用项目是 susu-dict；虚拟机名称与配置不依赖该项目。实际参数以各机 `/data/vm/<名称>/config/deployment.json` 和 `vm.py` 为准；修改配置或系统后，应同步更新本手册和[部署记录](WINDOWS-VM-DEPLOYMENT.md)。原安装决策见[计划](WINDOWS-VM-PLAN.md)，实施审阅见[审阅记录](WINDOWS-VM-REVIEW.md)。

## 1. 当前规格与状态

| 项目 | 开发机 | 干净验收机 |
|---|---|---|
| 名称与目录 | `win11-dev`；`/data/vm/win11-dev/` | `win11-clean`；`/data/vm/win11-clean/` |
| 用途 | 日常开发与调试 | 独立、干净的安装和验收测试；已做过重建演练 |
| Windows | Windows 11 Pro，英文版 25H2，build 26200.9457 | 同左 |
| 时区与分区 | Windows 时区 `Eastern Standard Time`；GPT/UEFI，NTFS 系统卷，独立 EFI 与 Recovery 卷；WinRE 已启用 | 同左 |
| vCPU / 内存 | 8 vCPU / 16 GiB | 4 vCPU / 8 GiB |
| 系统盘 | 120 GiB 稀疏 qcow2；VirtIO 块设备 | 80 GiB 稀疏 qcow2；VirtIO 块设备 |
| VM UUID | `8de9862e-a9f2-483c-aedd-d3019510b639` | `debc44ea-d35c-4f41-abe2-94f819043727` |
| 虚拟网卡 MAC | `52:54:00:71:11:01` | `52:54:00:71:11:02` |
| 日常 Windows 账户 | `arvin`，标准 Users 组 | 同左 |
| 维护 Windows 账户 | `vm-admin`，独立管理员账户 | 同左 |
| 交付状态 | 基础系统部署完成；Windows 激活由机主后续执行 | 同左 |

宿主当前为 Debian 12 x86-64，QEMU/KVM 7.2.22、swtpm 0.7.1；宿主用户 `arvin` 属于 `kvm` 组。`/data` 为 ext4，安装时核对的文件系统 UUID 是 `891031df-5cbc-4468-bd21-9253ef858834`。不要依赖 `/dev/sdX` 名称：设备名可能变化，启动脚本检查的是 UUID。两台机器可以分别启动；通常只开开发机，验收时再按宿主可用内存决定是否同时开 clean。控制器在启动前要求 `/data` 剩余空间大于 50 GiB，且可用内存大于目标 VM 内存加 9 GiB。

虚拟硬件为固定 `pc-q35-7.2`、1 socket、CPU `host`、KVM 加速、OVMF 4M UEFI Secure Boot、每机独立 swtpm TPM 2.0。显示为标准 VGA，经仅本机可访问的 SPICE UNIX socket 打开图形控制台；音频为 Intel HDA 双向设备，经 SPICE 转发。网络为 QEMU 用户态 NAT 与 VirtIO 网卡，具备出网/DNS；没有宿主桥接或宿主目录共享。两台 VM 的 RDP 已启用，NLA 必需；QEMU 在宿主回环地址转发 RDP，独立的 LAN 转发进程在 `192.168.1.31` 监听并仅接受 `192.168.1.0/24` 来源，端口见第 3 节。SPICE 自动剪贴板同步关闭。Windows 防火墙、UAC、Defender 实时保护开启。Guest Agent 已安装。BitLocker 自动预配显示 Protection Off、无 key protector，不能视为已启用 BitLocker 保护。

Windows 安装介质副本在每机 `iso/windows.iso`，英文 Windows 11 25H2 ISO SHA-256 为 `768984706b909479417b2368438909440f2967ff05c6a9195ed2667254e465e3`；`iso/virtio-win.iso` 为 VirtIO 0.1.302，SHA-256 为 `303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d`。这些是**当前介质**的识别值；更新介质时重新记录新哈希与来源。

## 2. 目录与配置

每台 VM 独占一个权限为 `0700` 的目录。重要路径如下，以下 `<名称>` 只能替换为 `win11-dev` 或 `win11-clean`：

| 路径 | 用途 |
|---|---|
| `config/deployment.json` | 资源、UUID、MAC、存储卷 UUID、安装状态与磁盘总线的实际配置 |
| `config/vm.py`、`config/README.txt` | 启动/停止/查看控制台的实现与本机操作说明 |
| `disks/system.qcow2` | 唯一 Windows 系统盘；不可在 VM 运行时从宿主改写 |
| `firmware/OVMF_CODE.fd`、`firmware/OVMF_VARS.fd` | 只读固件代码、此 VM 独占的可写 UEFI 变量 |
| `tpm/` | 此 VM 的 TPM 身份与持久状态；与系统盘、VARS 一起考虑恢复 |
| `run/` | PID、互斥锁与 QMP/SPICE/TPM/Guest Agent UNIX socket |
| `transfer/exchange.img` | 256 MiB FAT16 临时交换盘；不是备份，平时不挂载 |
| `backups/critical/` | 关键数据导出的本机暂存和加密维护凭据；与系统盘同处 `/data` |
| `evidence/`、`logs/` | 验收证据、QEMU/swtpm/启动日志 |

普通启动只接入系统盘，不接入 ISO 或交换盘。运行状态、配置和日志涉及每台 VM 的私有数据；不要把整个 `/data/vm` 提交到项目 Git 仓库。两台目录及维护凭据分别为 `0700`、`0600`，后续操作应维持这些权限。

## 3. 日常操作

以下命令由**宿主**用户 `arvin` 执行。将 `VM` 设为需要操作的名称；`view` 要求 VM 已启动。脚本会在需要时以 `kvm` 组运行，不需要用 root 启动 QEMU。

```bash
VM=win11-dev                 # 或 win11-clean
python3 /data/vm/$VM/config/vm.py status
python3 /data/vm/$VM/config/vm.py start
python3 /data/vm/$VM/config/vm.py view
python3 /data/vm/$VM/config/vm.py stop
```

仓库中的 `scripts/windows-vms.sh` 封装了两台 VM 的开机、正常关机、重启和状态查询。第二个参数可用 `dev`、`clean` 或 `all`；`all` 按开发机、验收机的顺序处理，重启时先让所选 VM 全部正常关机，再逐台启动。脚本沿用各机 `vm.py` 的启动资源检查与最多 120 秒的正常关机等待；关机超时会报错，不会强杀 QEMU。同时启动两台前确认宿主可用内存充足。

```bash
bash scripts/windows-vms.sh start dev
bash scripts/windows-vms.sh stop clean
bash scripts/windows-vms.sh restart all
bash scripts/windows-vms.sh status all
```

`status` 中 `qemu_alive`、`tpm_alive` 与 `rdp_proxy_alive` 表示宿主进程是否还在；运行时再看 `status` 和 `kvm.enabled`。`stop` 发送 ACPI 关机请求，最多等 120 秒；超时不会强杀 QEMU。此时通过控制台确认 Windows 是否仍在更新或弹出关机确认，再检查 `logs/launch.log`、`logs/qemu.log`、`logs/swtpm.log`，从 Windows 正常关机后重查状态。不要在 QEMU 持有系统盘时运行 `qemu-img check`、改写 qcow2、修改 VARS 或清理 TPM。

VM 启动并完成 Windows 登录服务初始化后，`192.168.1.0/24` 内的其他机器可直接 RDP 连接 `192.168.1.31:13488`（`win11-dev`）或 `192.168.1.31:13489`（`win11-clean`）。宿主机自身也可连接原有的 `127.0.0.1:13488/13489`。Windows 标准账户 `arvin` 已加入 `Remote Desktop Users`，RDP 要求 NLA，使用该机 Windows 账户密码登录。LAN 转发进程检查 TCP 客户端来源地址，不在 `192.168.1.0/24` 的来源会立即断开；它不在其他宿主地址监听。VM 关闭时对应端口不监听。若宿主有线 IP 改变，更新各机 `config/deployment.json` 的 `rdp_lan_ip` 后重启 VM；转发失败时查看 `logs/rdp-proxy.log`。SPICE 控制台仍可用于排障。

驱动维护时用 `start --drivers` 暂时接入 VirtIO ISO；文件交换用 `start --transfer` 暂时接入各机自己的 FAT 镜像。两者均需 VM 处于关机状态再启动。**宿主与 Windows 不得同时写交换镜像**。Windows 中完成复制后正常关机、确认 `qemu_alive=false`，再用 `mdir -i /data/vm/$VM/transfer/exchange.img ::` 查看或用 `mcopy -i ...` 导出。此镜像为无分区表的 FAT16 文件系统，不需要分区偏移。核对导出文件内容和 SHA-256 后，再清理交换盘中的临时副本；不要把它当成长期备份。安装模式 `start --install` 有已安装系统保护，会拒绝对当前两台机器直接重装。

Windows 日常使用 `arvin` 标准账户。涉及系统配置时，使用该机 `vm-admin` 并在操作结束后回到标准账户。维护凭据位于该机 `backups/critical/admin-credentials.enc`，以机主提供的该机初始 Windows 密码解密；准确命令在该机 `config/README.txt`。不要在项目文件、命令行参数、日志或工单中记录解密结果。原安装回答文件和带初始密码的安装 seed 已删除；自动登录已关闭。

## 4. 例行维护与变更记录

安装 Windows 更新或 VirtIO 驱动后，先正常重启来宾，再验证能登录、系统盘可见且 Healthy、网络/DNS 正常、Guest Agent 正常、Secure Boot/TPM 状态和设备错误列表。更新前后可记录 Windows build、驱动版本、变更日期及故障现象于本手册或专门的维护记录；证据文件放在对应 VM 的 `evidence/`，不包含密码、令牌或恢复密钥。系统盘剩余不足 20 GiB 或宿主 `/data` 剩余接近 50 GiB 时，先清理可再生成的文件，再评估是否扩盘。Windows 内删除文件不保证 qcow2 立即缩小。

当前安装包括 Windows、必要驱动、基础 Guest Agent，以及后续启用的 RDP/NLA。项目代码、开发工具链、GitHub CI/CD 和 BitLocker 保护尚未实施；将来按项目需要单独实施并记录实际变更。Windows 激活尚待机主处理，完成后应更新许可状态记录，勿把当前 Notification 状态误写为已激活。

## 5. 故障定位

| 现象 | 优先检查 | 处理边界 |
|---|---|---|
| `start` 拒绝 | `status`、`config/deployment.json`、`findmnt -no UUID -T /data/vm/$VM`、宿主可用内存与 `/data` 余量、`logs/launch.log` | 启动防护会拒绝重复启动、错误存储 UUID、内存/空间不足、状态文件缺失等；先修复原因，不删除锁或改 UUID 绕过检查 |
| 控制台打不开 | `status` 中 QEMU 是否存活、`run/spice.sock`、宿主 `remote-viewer`、`logs/qemu.log` | 控制台使用 UNIX socket，无外部 SPICE TCP 服务 |
| 无网络或 RDP | Windows 中检查 VirtIO 网卡、DNS、RDP 服务与防火墙；宿主检查 `127.0.0.1` 与 `192.168.1.31` 上的 `13488/13489` 监听、`rdp_proxy_alive` 和 `logs/rdp-proxy.log` | 当前仅 NAT；LAN RDP 转发只允许 `192.168.1.0/24` 来源，其他宿主地址不监听 |
| 磁盘或启动异常 | 先正常停止 VM，再查看日志、`qemu-img check` 和固件/TPM状态 | 不要在运行时修复磁盘；不要随意混用旧系统盘与新 VARS/TPM |
| 关机超时 | Windows 更新状态、电源按钮策略、Windows 事件日志、QEMU 日志 | `stop` 不会强杀；从来宾正常关机并重查进程 |
| 音频无声 | 先登录 Windows 交互式桌面，检查默认播放/录音设备和 SPICE 通道 | 锁屏下 Guest Agent 的 SYSTEM 会话音频测试曾近乎静音；宿主无物理扬声器/麦克风，交付只做过双向合成信号测试 |

两台交付时 `qemu-img check` 均无结构错误、Windows 更新后重启成功、Secure Boot/TPM 与 VirtIO 系统盘均经来宾检查。完整结果见[部署记录](WINDOWS-VM-DEPLOYMENT.md)和各机 `evidence/`。独立测试镜像曾验证 QEMU 在注入读错误且设置 `rerror=stop` 时进入 `io-error` 暂停状态；**现行主盘配置为 `rerror=report,werror=stop`**，该独立测试不等于现行主盘所有读故障都会自动暂停。遇到真实 I/O 错误应先保留日志和故障状态，停止进一步写入，评估数据导出或重建。

## 6. 关键数据与重建

代码应由未来项目的版本管理流程保存；本方案不做定期整机备份、快照或本地代码复制。不能再生成的资料、配置、许可证材料等，先通过 `--transfer` 导出到本机 `backups/critical/` 并核对哈希与可读性，再由机主放到受控的离机位置。**同一 `/data` 内的暂存不能防止整盘故障**。若日后启用 BitLocker 保护，先安全导出恢复密钥，再进行固件、TPM 或系统盘维护。

重建会销毁目标 VM 当前 Windows 系统和账户。具体已验证的 clean 重建边界、样例导出/恢复哈希、介质与顺序见[部署记录的“将来重建”](WINDOWS-VM-DEPLOYMENT.md#将来重建)及 clean 的 `evidence/rebuild-reset.json`、`evidence/rebuild-sample-restore-guest.txt`。开始前必须确认目标名称、VM 已关机、关键数据已离机验证；只替换**该机**的 `disks/system.qcow2`、`firmware/OVMF_VARS.fd`、`tpm/`，保留另一台 VM 和目标机的配置、介质、关键导出。旧 `vm-admin` 加密凭据对应旧系统，重装后需要重新创建并验证维护账户、轮换凭据。删除敏感安装 seed 后的手工安装分支已在控制器中支持，但没有作为本次重建演练路径验证；不要将它与已实测的自动重建混淆。
