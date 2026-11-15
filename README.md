# RO2RW PC

קובץ EXE אחד לווינדוס, שממיר את המחיצות שבתוך **super.img** של אנדרואיד מקריאה בלבד (EROFS או ext4 עם shared_blocks) ל-**ext4 לקריאה וכתיבה**, עם מקום פנוי לפי בחירתך.

הכול נעשה **על המחשב בלבד**: בלי טלפון, בלי ADB ובלי כלים חיצוניים. בסוף מקבלים super חדש לצריבה.

## הורדה

בדף **Releases** → `latest` → מורידים את **`RO2RW-PC.exe`**.
הקובץ נבנה אוטומטית ב-GitHub Actions בכל עדכון, אחרי שכל הבדיקות עוברות.

דרישות: Windows 10 או 11 (‎.NET Framework 4.8 מובנה בהם).

## שימוש

1. מפעילים את `RO2RW-PC.exe`.
2. בוחרים `super.img` או גוררים אותו לחלון. נתמכים raw, sparse וקבצים מפוצלים (`super.img.0`, `super.img.1`...).
3. מסמנים את המחיצות להמרה. ברירת המחדל היא כל מחיצות ה-EROFS וה-ext4.
4. בוחרים איך להגדיל: **MAX** (אחוזים מהמקום הפנוי, כמו ב-RO2RW) או **FIXED** (מספר MB קבוע לכל מחיצה).
5. לוחצים **התחל המרה**. הפלט נשמר בתיקייה `RO2RW_output` שליד הקובץ:
   - `super_rw.img`: ה-super החדש
   - `vbmeta_disabled.img`: נוצר אם יש `vbmeta.img` באותה תיקייה
   - `HOW_TO_FLASH.txt`: הוראות צריבה

## צריבה

```bash
fastboot flash super super_rw.img
```

```bash
fastboot flash vbmeta vbmeta_disabled.img
```

> ⚠️ גבו את המכשיר לפני הצריבה. האחריות על המשתמש. חובה לכבות את האימות (vbmeta).

## מה נתמך

| | |
|---|---|
| קלט super | raw, sparse, sparse מפוצל, מטא-דאטה LP בגרסאות 10.0–10.2 |
| EROFS | ללא דחיסה, inline, chunked, LZ4/LZ4HC, DEFLATE, ztailpacking, fragments, dedupe, big pcluster |
| ext4 | extents, block map, inline_data, 64bit, metadata_csum, shared_blocks |
| נשמר | הרשאות, בעלות, SELinux ‏(xattrs), capabilities, קישורים קשיחים וסמליים, התקנים |
| פלט | ext4 חדש (4K, extents, ללא journal), super חדש עם מטא-דאטה מעודכנת בכל החריצים |
| לא נתמך עדיין | EROFS עם LZMA או ZSTD |

ב-vendor נוספות אוטומטית שורות ext4 ל-`etc/fstab.*` לצד שורות ה-erofs.

## מבנה

- `src/Core`: הליבה (sparse, LP metadata, קוראי EROFS ו-ext4, כותב ext4, תהליך ההמרה)
- `src/App`: ממשק גרפי (WinForms, עברית)
- `tests`: בדיקות אוטומטיות שמשוות מול `mkfs.erofs`, `mke2fs`, `e2fsck` ו-`debugfs`
- `legacy-adb`: הגרסה הקודמת, שהפעילה את סקריפט RO2RW המקורי בטלפון דרך ADB

רעיון ההגדרות (MAX/FIXED, S/V/P/SE/OT) מבוסס על RO2RW מאת LeeGarChat.
