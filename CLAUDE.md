# Claude Code Instructions

Before editing, reviewing, or answering technical questions about this repository, read:

1. `AGENTS.md`
2. `README.md`
3. `CHANGELOG.md`
4. `docs/OPERATIONS.md`
5. `docs/TROUBLESHOOTING.md`

Treat `AGENTS.md` as the source of truth for build commands, safety rules, mounted-drive handling, and rclone/WinFsp workflow.

Do not scan broad mounted FTP/SFTP roots such as `Z:\public_html` or `\\server\...\public_html` unless the user asks for a specific path. Prefer reading exact files or small subdirectories.
