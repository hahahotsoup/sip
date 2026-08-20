# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.0] - 2026-08-20

### Added

- **Tree Comments**: `sip ingest tree <id>` command to view tree-structured comments using FragmentId field and recursive CTE queries
- **Multi-tag System**: Tags and EvidenceTags tables for many-to-many tag associations
  - `sip ingest tag list` - List all tags
  - `sip ingest tag add <evidenceId> <tagName>` - Add tag to evidence
  - `sip ingest tag rm <evidenceId> <tagName>` - Remove tag from evidence
  - `sip ingest list --tag <tagName>` - Filter evidence by tag

### Changed

- Evidence table now includes additional fields for tree comments and multi-tag support:
  - FragmentId (tree comments parent reference)
  - Platform, ContentId, Author, CanonicalUrl
  - Context, Snapshot, Note
  - WatchEnabled, WatchInterval, WatchLastCheckedAt, WatchLastHash

### Database Migration

- Added Phase2 migration in simon.cs for new Evidence fields
- Created Tags table for tag management
- Created EvidenceTags table for many-to-many associations
- Added indexes for FragmentId and Platform fields

## [1.4.0] - 2026-08-20

### Added

- **Ingest Stats**: `sip ingest stats` command to display one-line summary of evidence library (total evidence, versions, modified, reversed, topics, tags, new today)
- **Stale Cleanup**: `sip ingest cleanup --stale` command to clean up stale evidence
  - `--min-views N` - Minimum view count to keep (default: 3)
  - `--recent-days N` - Days to consider "recently viewed" (default: 7)
  - `--dry-run` - Preview deletion without actually deleting
  - `--yes` - Skip confirmation prompt
  - Respects ViewCount and LastViewedAt fields
- **Watch Commands**: `sip ingest watch` subcommands for web monitoring
  - `sip ingest watch add <id> [--interval <min>]` - Enable monitoring (minimum 5 min interval)
  - `sip ingest watch rm <id>` - Disable monitoring
  - `sip ingest watch list` - List all watch targets
  - `sip ingest watch refresh [id] [--all]` - Manually refresh monitored content
- **Semantic Diff**: `sip --diff --semantic` option to show semantic distance and change grade
  - Displays semantic distance (0-1, lower = more similar)
  - Shows change grade: ⚪ polish (minor wording), 🟡 adjust (content change), 🔴 reverse (stance change)
  - Detects stance reversal signals

### Database Migration

- Added ViewCount and LastViewedAt fields to Evidence table
- Watch columns (WatchEnabled, WatchInterval, WatchLastCheckedAt, WatchLastHash) now functional

### Tests

- Added 26 new tests (IngestPhase2Tests, WatchCliTests, SemanticDiffTests)
- Total test count: 97 (up from 71)

## [1.2.0] - 2026-08-15

### Added

- **Ingest Module**: `sip ingest` command for collecting non-RSS information
  - `sip ingest --stdin` - Store evidence from stdin
  - `sip ingest --url` - Store evidence from URL
  - `sip ingest --evidence` - Import evidence packs
  - `sip ingest list` - List evidence
  - `sip ingest show <id>` - Show evidence details
  - `sip ingest confirm <id>` - Verify evidence
  - `sip ingest rm <id>` - Remove evidence
  - `sip ingest refresh` - Refresh watch targets
  - `sip ingest group` - Topic grouping
  - `sip ingest retrieve` - Evidence search
  - `sip ingest ask` - Answer from evidence

- **Version Tracking**: Track changes with diff detection and grade levels (polish/adjust/reverse)
- **Semantic Search**: AI-powered similarity search for evidence
- **Full-text Search**: Grep-like search without AI dependency
- **Today's Soup**: Daily recommendations based on reading history
- **Security Guardian (simon)**: Default-on security with three protection levels

### Changed

- Evidence table created for non-RSS information storage
- WatchTargets table created for web monitoring
- Groups table created for topic organization

## [1.1.0] - 2026-08-01

### Added

- **RSS Feed Management**: Add, remove, and update RSS subscriptions
- **Article Reading**: TUI interface for reading articles
- **Full-text Search**: Search articles by keywords
- **Semantic Search**: AI-powered similarity search
- **Today's Soup**: Daily recommendations
- **Telemetry**: Optional local reading behavior tracking

### Changed

- Initial release with core RSS reading functionality

## [1.0.0] - 2026-07-15

### Added

- **Core Architecture**: SQLite-based local-first design
- **Feed Management**: RSS feed subscription and management
- **Article Storage**: Article content and metadata storage
- **Basic Search**: Simple text search functionality

### Changed

- Initial release
