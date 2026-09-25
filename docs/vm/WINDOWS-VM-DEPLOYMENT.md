# Windows 虚拟机部署记录

> 上级：[虚拟机](README.md) · 根目录：[README](../../README.md)  
> 敏感值（IP、账户、UUID、私有链接等）以 `<占位符>` 表示，实际值见本地 [SENSITIVE.md](../SENSITIVE.md)（已 gitignore，不入库）。

2026-09-21（America/Toronto）。按 [安装计划](WINDOWS-VM-PLAN.md) V5 部署。后续开发、日常维护和故障处理请先读[运行与维护手册](WINDOWS-VM-OPERATIONS.md)。两台虚拟机的全部专属状态在各自的 `/data/vm/<名称>/` 中；本次仅部署 Windows 基础系统，GitHub、开发工具链与项目配置不在范围内。Windows 激活由机主在本任务完成后执行，不作为本次验收条件。

| 虚拟机 | 目录 | vCPU / 内存 | 虚拟系统盘 | Windows / 更新 | 磁盘控制器 |
|---|---|---:|---:|---|---|
| 开发机 | `/data/vm/win11-dev` | 8 / 16 GiB | 120 GiB | Windows 11 Pro 25H2，26200.9457 | VirtIO |
| 干净验收机 | `/data/vm/win11-clean` | 4 / 8 GiB | 80 GiB | Windows 11 Pro 25H2，26200.9457 | VirtIO |

日常均以宿主 `<OWNER_USER>` 运行 `python3 /data/vm/<名称>/config/vm.py start`，控制台用 `.../vm.py view`，查询 `.../vm.py status`，正常关机 `.../vm.py stop`。各机 `config/README.txt` 有准确命令、日志位置和维护凭据的本机解密方法。启动脚本在缺少 KVM、存储 UUID 不符、内存/磁盘不足、已有同盘进程或已安装系统被要求再次 `--install` 时拒绝操作；普通启动不挂载 ISO 或交换盘。关机超时不强制杀 QEMU，先在 Windows 内确认电源按钮策略后重试。需要交换文件时仅显式 `start --transfer`，QEMU 完全停止后才可由宿主用 mtools 读写该机的 FAT 交换镜像。

官方英文 Windows 11 25H2 ISO SHA-256：`768984706b909479417b2368438909440f2967ff05c6a9195ed2667254e465e3`。VirtIO 0.1.302 ISO SHA-256：`303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d`。两台各自的副本均于交付审计时复核。配套 OVMF 4M Secure Boot 固件与独立 Microsoft 密钥 VARS、独立 swtpm TPM 2.0 已实际启动验证；QMP 查询 KVM 为 enabled。主盘先经 SATA 安装，使用 64 MiB VirtIO 测试盘验证驱动后安全切换。运行后的 Windows `Get-Disk` 显示系统盘为 `Red Hat VirtIO`、Healthy。

两台均检查 Secure Boot=True、TPM Ready=True、设备错误列表为空、Guest Agent 运行、NAT 网络和 DNS 可用、WinRE 已启用、音频设备/显示设备正常、Windows Update 成功且重启后无待重启键。三类 Windows 防火墙、UAC 与 Defender 实时保护均开启。Windows Pro 许可状态为 5（[Notification](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/ee957718%28v%3Dvs.85%29)，尚待机主激活），默认 RDP 关闭。日常账户 `<OWNER_USER>` 仅在 Users 组；独立 `<VM_ADMIN>` 已在降权前验证可登录。维护密码保存在各机 `backups/critical/admin-credentials.enc`，以机主提供的该机初始密码为解密口令，未写进文档或源码。安装回答文件和带初始凭据的 seed 镜像已删除，自动登录关闭，注册表 `DefaultPassword` 不存在。系统自动预配的 BitLocker 显示已用空间加密、Protection Off、没有 key protector；这不代表磁盘已有 BitLocker 保护，也没有恢复密钥可交付。详细安全、DNS、激活状态见各机 `evidence/activation-dns-security.txt` 与 `defender-final.txt`。

后续按机主要求，两台 VM 均启用 RDP 和 NLA，将标准账户 `<OWNER_USER>` 加入 Windows 的 `Remote Desktop Users`，启用对应防火墙规则。QEMU 用户态 NAT 仅在宿主回环地址转发：`win11-dev` 使用 `127.0.0.1:<RDP_PORT_DEV> → 3389`，`win11-clean` 使用 `127.0.0.1:<RDP_PORT_CLEAN> → 3389`。两台均在 Windows 内验证 RDP/NLA 注册表状态、TermService、账户授权、防火墙规则和 TCP 3389 监听，并从宿主端口收到 RDP 协议响应；开发机还在关机重启后再次验证协议响应。未进行带账户密码的完整交互登录测试。验证结束后两台均正常关机。当前连接方式见[运行与维护手册](WINDOWS-VM-OPERATIONS.md#3-日常操作)。

再按机主要求开放 `<LAN_SUBNET>` 局域网直接连接：保持上述 QEMU 回环地址转发，在每台 VM 控制器启动时运行独立的来源过滤转发进程，分别监听 `<HOST_LAN_IP>:<RDP_PORT_DEV>/<RDP_PORT_CLEAN>`，只把源地址属于该网段的 TCP 连接送到 QEMU 回环端口。两台都验证了允许来源 `<HOST_LAN_IP>` 获得 RDP 协议响应、非允许来源 `<DENIED_TEST_IP>` 被断开。配置时开发机已在运行，因此无中断地启动了转发进程；验收机正常启动验证后关闭。没有从另一台物理 LAN 机器做交互式登录测试。

开发机的系统及账户最终检查在 `evidence/windows-final-check.txt`。clean 的更新、真实重启、VirtIO 和最终普通启动分别在 `evidence/rebuild-windows-update-pass1.txt`、`rebuild-reboot-audit-final.txt`、`rebuild-windows-post-virtio-final.txt`、`rebuild-final-normal-start.txt`。两台虚拟机已分别正常重启、关机、重新启动；完成审计时均关闭，无 QEMU/swtpm 遗留。两块 qcow2 经 `qemu-img check` 无结构错误；实际占用约 32.5/35.7 GiB，两个 VM 目录连同 ISO 和临时文件约 42/45 GiB，`/data` 尚有约 1.4 TiB。

clean 已完成真实重建演练：在旧系统创建 75 字节无敏感样例，通过专属 FAT 交换盘导出到 `backups/critical/rebuild-sample/`，在 guest、交换盘清单及宿主复核 SHA-256 均为 `18617b52166d1c017bf977aeb189a860a9206f04161fd3193c2a7cb639b3a91c`；仅重置 clean 的系统 qcow2、OVMF VARS 和 TPM 状态，保留配置、备份、交换盘、ISO 和证据；重新安装 Windows、驱动、更新与账户，再把样例导回新系统。恢复后 SHA-256 与导出前一致。重置边界见 `evidence/rebuild-reset.json`，恢复结果见 `evidence/rebuild-sample-restore-guest.txt`。不保留完整磁盘备份或快照。

两台均在无物理声卡的宿主上完成 SPICE 双向合成音频测试。开发机播放 Windows 自带 WAV 到宿主 `auto_null.monitor`，8 秒有 546676 个非零样本、峰值 11658；反向向 Windows Line In 注入 880 Hz 音，1916274 个样本中 1901149 个非零、峰值 14862。clean 在标准用户 `<OWNER_USER>` 登录桌面后，播放 880 Hz 音得到 585724 个非零样本、峰值 12300；反向录音 1916274 个样本中 1903828 个非零、峰值 15043。两台各自的 `evidence/audio-loopback-validation.json` 保存了量化指标。clean 在锁屏状态下用 Guest Agent 的 SYSTEM 会话播放/采集会得到近乎静音的数据，音频功能测试需先登录交互式桌面。宿主没有物理扬声器或麦克风，因此未做人工听音。

安装期间发现回答文件分区操作顺序必须连续，已在空白盘重试前修复；UEFI 首次从光盘启动需在提示窗口按 Enter。首次 ACPI 关机请求在 Windows 刚更新后的部分会话曾超时，核对并重新应用电源按钮“关机”策略后重试成功，未强制终止进程。重建后的 Windows Update 有五项全部成功，需由 `Restart-Computer -Force` 完成真实重启；事件日志和启动时间已确认。以上历史不影响当前交付状态，但重建时应按证据逐项复核。

故障路径另用独立 8 MiB 原始测试镜像验证：[QEMU `blkdebug`](https://www.qemu.org/docs/master/devel/testing/blkdebug.html) 在第二扇区读取时注入 EIO，配合 `rerror=stop` 后 QMP `query-status` 为 `io-error`、`running=false`，`query-block` 为 `io-status=failed`，并未写入两台系统盘。见 clean 的 `evidence/isolated-disk-fault.json`。重复启动、错误存储 UUID、内存不足、缺 ISO 拒绝分别见 `evidence/start-guards.json` 和 `pre-os-probe.json`。控制器错误返回非零；QEMU 异常暂停时先检查 `status` 与 `logs/`，不能自动重启或强制退出。

## 将来重建

重建步骤已移至[运行与维护手册 § 6.1](WINDOWS-VM-OPERATIONS.md#61-重建步骤)。本记录只保留本次重建演练的证据：clean 的 `evidence/rebuild-reset.json`、`evidence/rebuild-sample-restore-guest.txt`。
