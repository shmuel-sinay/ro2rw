# MTK Flash Tool for macOS

**הכלי הראשון לצריבת מכשירי MediaTek ישירות על Mac - בלי Windows!**

## התקנה (פשוט מאוד):

### שלב 1 - התקנה:
1. **חלצו את ה-zip**
2. **לחצו לחיצה כפולה** על `setup.command`
3. אם macOS חוסם - לחצו קליק ימני → Open
4. חכו שההתקנה תסתיים (כמה דקות)

### שלב 2 - הרצה:
**לחצו לחיצה כפולה** על `Run MTK Flash Tool.command`
(תתבקשו להזין סיסמת Mac - זה נדרש לגישת USB)

## דרישות:
- macOS 12+ (Monterey ומעלה)
- חיבור אינטרנט (להתקנה)
- 500MB שטח פנוי

**הכל אוטומטי!** הsetup מתקין:
- Homebrew (מנהל חבילות)
- Python 3
- libusb (לגישת USB)
- PySide6 (ממשק גרפי)
- pyusb + pyserial

## שימוש:

1. חברו את המכשיר ל-USB (במצב BROM/Preloader - כבוי + Vol+ תוך חיבור)
2. לחצו `Scatter-loading` ובחרו את קובץ ה-scatter של ה-ROM
3. קובץ ה-DA נטען אוטומטית
4. סמנו את המחיצות לצריבה
5. לחצו `Download`

## קרדיט:
פותח על בסיס ניתוח SP Flash Tool ו-mtkclient.
