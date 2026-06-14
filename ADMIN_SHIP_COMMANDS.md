# Ship Save Admin Commands Documentation

## Overview
Administrative commands for monitoring and moderating the ship saving system. All commands require Admin permissions.

---

## `shipsave_list [player_name]`

**Purpose**: List all ship save files with detailed information

**Usage**:
```
shipsave_list                    # List all ship saves
shipsave_list "John Doe"         # List saves for specific player
```

**Output Example**:
```
Found 15 ship save files:
  MyShip_20241216_143022.yml:
    Ship Name: USS Enterprise
    Player ID: 12345678-1234-1234-1234-123456789abc
    Timestamp: 2024-12-16 14:30:22
    File Size: 847.3 KB
    Entities: 298
    Containers: 23
    Contained Items: 156

  CargoBarge_20241215_091545.yml:
    Ship Name: Cargo Hauler
    Player ID: 87654321-4321-4321-4321-ba9876543210
    Timestamp: 2024-12-15 09:15:45
    File Size: 1,245.7 KB
    Entities: 445
    Containers: 31
    Contained Items: 203
```

**Use Cases**:
- Monitor server storage usage
- Find saves by specific players
- Identify large or unusual save files

---

## `shipsave_inspect <filename>`

**Purpose**: Deep inspection of ship save file contents and structure

**Usage**:
```
shipsave_inspect "MyShip_20241216_143022.yml"
```

**Output Example**:
```
=== Ship Save Inspection: MyShip_20241216_143022.yml ===
Ship Name: USS Enterprise
Player ID: 12345678-1234-1234-1234-123456789abc
Original Grid ID: 54321
Timestamp: 2024-12-16 14:30:22 UTC
Format Version: 1
Checksum: G54321:T847[Wall423,Floor234]:E298:C23x156:CM89[Sol12,Sta8,Bat6]:P789423

Grid 1:
  Grid ID: 54321
  Tiles: 847
  Total Entities: 298
  Container Entities: 23
  Contained Entities: 156
  Has Atmosphere Data: False
  Has Decal Data: True
  Top Component Types:
    TransformComponent: 298
    MetaDataComponent: 298
    SolutionContainerManagerComponent: 45
    IFFComponent: 1
    StackComponent: 23
  Top Entity Types:
    WallSteel: 156
    FloorSteel: 234
    Locker: 12
    ChemMaster: 3
    Paper: 23
```

**Use Cases**:
- Diagnose save file issues
- Analyze save complexity and content
- Verify component preservation
- Debug reconstruction problems

---

## `shipsave_validate <filename>`

**Purpose**: Validate save file integrity, checksums, and structure

**Usage**:
```
shipsave_validate "MyShip_20241216_143022.yml"
```

**Output Example**:
```
=== Validation Results for MyShip_20241216_143022.yml ===
✓ YAML parsing: SUCCESS
✓ Checksum validation: SUCCESS
✓ Structure validation: SUCCESS

Grid 1 Validation:
  Container Entities: 23
  Contained Entities: 156
  ✓ Total Entities: 298
  ✓ Total Tiles: 847
```

**Error Example**:
```
=== Validation Results for CorruptShip_20241210_120000.yml ===
✓ YAML parsing: SUCCESS
❌ VALIDATION FAILED: Checksum mismatch! Ship data may have been tampered with.
```

**Use Cases**:
- Verify save file integrity before allowing loads
- Detect tampered or corrupted files
- Troubleshoot loading issues
- Check container relationship consistency

---

## `shipsave_delete <filename>`

**Purpose**: Safely delete problematic or unwanted ship save files

**Usage**:
```
shipsave_delete "CorruptShip_20241210_120000.yml"
```

**Output Example**:
```
Successfully deleted ship save file 'CorruptShip_20241210_120000.yml'
```

**Error Example**:
```
Ship save file 'NonExistent.yml' not found.
```

**Use Cases**:
- Remove corrupted saves
- Delete inappropriate ship names
- Clean up test saves
- Remove saves from banned players

---

## `shipsave_cleanup [options]`

**Purpose**: Automated cleanup of old or corrupted ship save files

**Options**:
- `--dry-run`: Show what would be deleted without actually deleting
- `--older-than-days=N`: Set age threshold (default: 30 days)

**Usage**:
```
shipsave_cleanup --dry-run                           # Preview cleanup
shipsave_cleanup --older-than-days=60                # Delete files older than 60 days
shipsave_cleanup --dry-run --older-than-days=7       # Preview deleting files older than 7 days
shipsave_cleanup                                     # Delete old/corrupted files (default 30 days)
```

**Output Example**:
```
=== Ship Save Cleanup Analysis ===
Old files (>30 days): 8
Corrupted files: 2
Total files to remove: 10

DRY RUN - No files will be deleted

Old files that would be deleted:
  OldShip_20241115_080000.yml
  TestShip_20241110_150000.yml
  ...

Corrupted files that would be deleted:
  CorruptShip_20241210_120000.yml
  BadChecksum_20241205_090000.yml

Successfully deleted 10 ship save files
```

**Use Cases**:
- Regular server maintenance
- Free up disk space
- Remove corrupted saves automatically
- Clean up test/development saves

---

## Common Administrative Workflows

### **Daily Monitoring**
```bash
# Check current save situation
shipsave_list

# Preview what cleanup would remove
shipsave_cleanup --dry-run
```

### **Player Complaint Investigation**
```bash
# Find player's saves
shipsave_list "PlayerName"

# Inspect specific save
shipsave_inspect "PlayerShip_20241216_143022.yml"

# Validate integrity
shipsave_validate "PlayerShip_20241216_143022.yml"
```

### **Storage Management**
```bash
# Preview cleanup of files older than 60 days
shipsave_cleanup --dry-run --older-than-days=60

# Actually clean up old files
shipsave_cleanup --older-than-days=60
```

### **Corruption Response**
```bash
# Validate all saves for a specific player
shipsave_list "SuspiciousPlayer" 
# Then validate each file individually

# Remove confirmed corrupted file
shipsave_delete "CorruptedShip.yml"
```

---

## File Locations

**Save Directory**: `{UserData}/saved_ships/`
**File Format**: `{ShipName}_{YYYYMMDD}_{HHMMSS}.yml`

## Permissions

All commands require `AdminFlags.Admin` permission level.

## Error Handling

- Commands gracefully handle missing files
- Validation errors are clearly reported
- Partial failures are logged but don't crash commands
- File permission issues are reported clearly

## Performance Notes

- `shipsave_list` scans all files and may be slow with many saves
- `shipsave_inspect` performs deep analysis and may take time on large saves
- `shipsave_cleanup` with `--dry-run` is safe to run frequently
- Commands are optimized to avoid loading full save data when possible