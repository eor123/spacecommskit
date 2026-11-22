# SpaceCommsKit traffic stats

This branch keeps GitHub traffic history for `eor123/spacecommskit` and `eor123/spacecommskit.com`. GitHub only keeps 14 days of traffic, so a weekly job saves a copy here. It holds only data, no code.

| File | Contents |
|---|---|
| `traffic_daily.csv` | `date,repo,clones,clone_uniques,views,view_uniques`. One row per repo per day. Each run refreshes recent days and keeps older ones. |
| `snapshots.csv` | `run_date,repo,stars,forks,watchers`. One row per repo per run. |
| `release_downloads.csv` | `run_date,repo,tag,asset,download_count`. Cumulative download count per release asset, as of each run. |
| `referrers.csv` | `run_date,repo,referrer,count,uniques`. Top referrers (14-day window), as of each run. |
| `paths.csv` | `run_date,repo,path,count,uniques`. Most popular paths (14-day window), as of each run. |
