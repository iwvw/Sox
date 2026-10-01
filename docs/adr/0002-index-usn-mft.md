# ADR-0002. 索引引擎：NTFS USN Journal + MFT，全盘纯文件名

日期：2026-09-29
状态：Accepted

## 背景

核心目标是「全盘文件名秒搜」。Everything 的杀手锏是深度利用 NTFS 特性实现零后台进程的实时索引。需求确认：全盘、不搜文件内容、低内存。

## 决策

- 索引源：NTFS USN Journal（增量）+ 首次 MFT 直读全量扫描
- 范围：全盘 NTFS 卷；不建全文索引
- 存储：全部索引驻留内存，序列化落盘为「索引缓存」供重启快速恢复
- 增量：USN 事件驱动（CREATE / DELETE / RENAME / CLOSE），零轮询 CPU
- 每卷记录 `journal_id` + `usn_start` 断点，用于续读与陈旧判定

数据结构：定长 `FileEntry` struct + 字符串池；前缀 Trie（lower_name）+ 拼音表 + 主路径表三路索引。子串匹配不建索引，靠 200 万条线性扫（~30MB，5-10ms）兜底。

## 后果

- 限 NTFS 卷，FAT/exFAT 无法索引（PRD 记录为 P2 降级方案）
- 需要管理员权限读取卷设备（ADR-0004）
- 内存预算：200 万文件约 63MB（FileEntry+字符串池 35 + 拼音 10 + 目录树 10 + Trie 8）
- 获得毫秒级查询与实时增量，符合 Everything 级体验
