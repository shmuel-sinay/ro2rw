# RO2RW PC

קובץ EXE אחד לווינדוס לעריכת רום של אנדרואיד: RO2RW (‏super לקריאה וכתיבה), Root עם Magisk, ביטול AVB והסרת Orange State.

הכול נעשה **על המחשב בלבד**: בלי טלפון, בלי ADB ובלי כלים חיצוניים. בסוף מקבלים super חדש לצריבה.

## הורדה

בדף **Releases** → `latest` → מורידים את **`RO2RW-PC.exe`**.
הקובץ נבנה אוטומטית ב-GitHub Actions בכל עדכון, אחרי שכל הבדיקות עוברות.

דרישות: Windows 10 או 11 (‎.NET Framework 4.8 מובנה בהם).

## שימוש: פרויקט לכל מכשיר

1. **פרויקט חדש**: נותנים שם, בדרך כלל שם המכשיר. נוצרת תיקייה `Projects\<שם>` ליד התוכנה.
2. **הוספת קבצים**: אחת משתי דרכים:
   - **שאיבה מהמכשיר** (MediaTek): כפתור "שאב רום מהמכשיר". mtkclient מובנה בתוכנה, שואב את כל המחיצות לתוך הפרויקט בשמות המחיצות (`boot_a.img`, `lk_a.img`...), ויוצר גם `gpt.bin`, קובץ scatter ויומן. בפעם הראשונה לוחצים "דרייבר USB" כדי להתקין את UsbDk.
   - **קבצים קיימים**: כפתור "הוסף קבצים" או גרירה לחלון.

   התוכנה מזהה כל קובץ לבד:

| קובץ | עריכה |
|---|---|
| `super.img` (raw, sparse או מפוצל) | **RO2RW**: ממיר את המחיצות ל-ext4 לקריאה וכתיבה, עם מקום פנוי (MAX או FIXED) |
| `boot.img` / `init_boot.img` | **Root עם Magisk** (מובנה): עריכה על המחשב, זהה לאפליקציית Magisk. ה-ABI מזוהה מקובץ init שב-ramdisk |
| `vbmeta*.img` | **ביטול AVB**: verity ו-verification כבויים (הלוגיקה של AVB-Disabler) |
| `lk*.img` / `lk*.bin` | **הסרת Orange State**: ביטול ההשהיה של 5 שניות ומחיקת טקסט האזהרה. גרסת האנדרואיד מזוהה אוטומטית |

   כברירת מחדל מסומנים רק קבצי חריץ A, רק vbmeta הראשי, ו-init_boot במקום boot כשקיים.

3. **בצע עריכה**: הקבצים הערוכים נשמרים בתיקייה **`ערוך`** בתוך הפרויקט, עם אותם שמות. המקור לא משתנה. נוצרים גם `HOW_TO_FLASH.txt` וקובץ **scatter ל-SP Flash Tool**, שבו מסומנות לצריבה רק המחיצות שנערכו. ה-scatter נבנה מה-scatter של הקושחה אם הוא בפרויקט, ואם לא, מ-`gpt.bin` שנוצר בשאיבה.

## צריבה

```bash
fastboot flash super super.img
```

```bash
fastboot flash vbmeta vbmeta.img
```

(מתוך התיקייה `ערוך`. אפשר לצרוב גם ב-SP Flash Tool או ב-mtkclient.)

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

- `src/Core`: הליבה (sparse, LP metadata, EROFS/ext4, כותב ext4, boot image, cpio, Magisk, LK, vbmeta, פרויקטים)
- `src/App`: ממשק גרפי (WinForms, עברית)
- `tests`: בדיקות אוטומטיות שמשוות מול `mkfs.erofs`, `mke2fs`, `e2fsck` ו-`debugfs`
- `legacy-adb`: הגרסה הקודמת, שהפעילה את סקריפט RO2RW המקורי בטלפון דרך ADB

רעיון ההגדרות (MAX/FIXED, S/V/P/SE/OT) מבוסס על RO2RW מאת LeeGarChat.

מבוסס על: RO2RW מאת LeeGarChat · ‏Magisk מאת topjohnwu (‏boot_patch.sh ו-magiskboot) · ‏AVB-Disabler · ‏hex-replacer · המדריך של hovatek ל-Orange State.

## רכיבים מובנים

ה-EXE כולל שני כלים חיצוניים. שניהם נבנים או נלקחים בזמן הבנייה ב-GitHub Actions ממקור רשמי:
- [mtkclient](https://github.com/bkerler/mtkclient) מאת bkerler (רישיון GPLv3): נבנה מקוד המקור בקומיט קבוע, ומחולץ לתיקייה `tools\mtkclient` בשימוש הראשון.
- [Magisk](https://github.com/topjohnwu/Magisk) מאת topjohnwu (רישיון GPLv3): ה-APK הרשמי מהמהדורה האחרונה. מי שרוצה גרסה אחרת יכול לבחור APK בלשונית Magisk.
