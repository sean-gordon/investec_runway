# Git maintenance: Daily commit squash 🦓

To keep the project history clean, we only keep the latest commit for each calendar day. This prevents the logs from being cluttered with incremental development while preserving the project state at the end of each working day.

## How to squash history

This process involves creating a temporary branch, applying the final commit from each day, and then replacing the main branch.

**⚠️ Warning:** This operation rewrites git history. Ensure you have a backup or have pushed your current state to GitHub before proceeding.

### Steps

1. **Identify the commits to keep**
   Run this to see the latest commit hash for each day:
   ```powershell
   git log --format="%ad %H" --date=short | Group-Object { $_.Split(' ')[0] } | ForEach-Object { $_.Group[0].Split(' ')[1] }
   ```

2. **Create a temporary branch**
   Start from the first commit to maintain history, or use an orphan branch for a fresh start.
   ```bash
   git checkout --orphan temp-main
   git rm -rf .
   ```

3. **Apply the daily commits**
   In chronological order, cherry-pick the identified hashes:
   ```bash
   git cherry-pick <hash-from-day-1>
   git cherry-pick <hash-from-day-2>
   ```
   *Note: If a cherry-pick causes conflicts, use `git checkout <hash> -- .` followed by `git commit` to manually apply that day's state.*

4. **Swap branches**
   ```bash
   git branch -D main
   git branch -m main
   ```

5. **Force push to GitHub**
   ```bash
   git push origin main --force
   ```

## Automated PowerShell script

Use this script to automate the selection and application of daily state:

```powershell
# Get latest hashes per day (ordered by date ascending)
$hashes = git log --format="%ad %H" --date=short | 
          Group-Object { $_.Split(' ')[0] } | 
          Sort-Object Name | 
          ForEach-Object { $_.Group[0].Split(' ')[1] }

# Create new orphan branch
git checkout --orphan daily-squash
git rm -rf .

foreach ($hash in $hashes) {
    # Get the date and message for the commit
    $info = git log -1 --format="%ad | %s" --date=short $hash
    Write-Host "Applying state for $info"
    
    # Restore the state of the repo at that commit
    git checkout $hash -- .
    git commit -m "daily state: $info"
}
```

---
*Last Updated: 14 April 2026*
