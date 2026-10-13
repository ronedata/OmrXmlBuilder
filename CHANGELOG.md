# OMR XML Builder — Changelog

সব উল্লেখযোগ্য পরিবর্তন এখানে থাকবে।

Version নিজে থেকেই বাড়ে: কোনো source file বদলানোর পর **Release** build করলে
patch number এক বাড়ে (`Properties/AssemblyInfo.cs`) আর
`releases/OmrXmlBuilder_v{version}.exe` নতুন নামে জমা হয়। কোড না বদলে rebuild
করলে version একই থাকে, আর Debug build কিছুই বদলায় না।
Minor/major release-এর জন্য নিজে চালান:
`powershell -File tools\BumpVersion.ps1 -Part minor`

Format: [major.minor.patch] — YYYY-MM-DD

---

## [1.0.2] — 2026-08-31

### Added
- MCQ Answer button → Custom-এর মতো click করলে প্রশ্ন সংখ্যা ও option চায়
  (option লিখলেই charSet কী হবে সাথে সাথে দেখায়), তারপর sheet-এ click করলে
  ওই মাপেই block বসে। নিচের ঘর দুটো আগের মতোই আছে — বসানো block সরাসরি এডিট করতে
- Element lock (Ctrl+L, বা list-এর নিচের বোতাম) — locked element টেনে, handle
  দিয়ে, arrow key দিয়ে সরে না, double-click-এ direction-ও বদলায় না।
  List-এ `[L]`, canvas-এ কাটা-কাটা border। Select করা যায় (তাই unlock করা যায়)
  ও Properties থেকে এডিট করা যায়। Undo (Ctrl+Z) lock-ও ফেরায়।
  Duplicate / page-copy করলে কপিটা unlocked হয়। XML-এ lock যায় না

---

## [1.0.1] — 2026-08-31

### Added
- App icon — একটা OMR sheet (corner anchor + timing mark, চার row-এর পাশে চারটা
  left marker, প্রতি row-এ একটা ভরা bubble), Icons.cs-এর সেই palette-এ আঁকা
- Explorer / taskbar-এ exe-র নিজের icon (`<ApplicationIcon>`), আর MainForm,
  Check OMR, Generated XML ও Copy-page dialog — সব window-এর title bar-এ একই icon
- `tools\MakeAppIcon.ps1` — icon-টা কোড থেকেই আঁকে ও multi-size `app.ico` লেখে
  (16/24/32/48/64/96 DIB + 256 PNG); ডিজাইন বদলাতে হলে script এডিট করে আবার চালান
- Custom button → click করলে memberName, charSet ও lines চায়, তারপর sheet-এ
  click করলে ওই মান নিয়েই region বসে। charSet-এ কমা থাকলে (S,H,E)
  charSetSeparators নিজে থেকেই `,` হয়। শেষবারের মান মনে রাখে

- Auto version bump — source বদলানোর পর Release build করলে patch number নিজেই
  বাড়ে (csproj-এর `BumpVersion` target + `tools\BumpVersion.ps1`)

### Changed
- Rotation marker-এর display name canvas ও element list-এ এখন "Rot. Top" /
  "Rot. Bottom" (আগে XML লোড করলে tag-এর নাম `rotate180Detection` দেখাত)।
  শুধু দেখানোর নাম — memberName ও generated XML অপরিবর্তিত

### Fixed
- Debug build আর `releases/` ফোল্ডারে লিখবে না। আগে `StampVersionedExe`-এ
  configuration-এর শর্ত ছিল না, তাই Debug build তার unoptimised exe একই version
  নামে release ফাইলের উপর বসিয়ে দিত

---

## [1.0.0] — 2026-08-23

প্রথম version-managed release। এ পর্যন্ত তৈরি সব feature এতে আছে।

### Core — Designer
- OMR image (এক বা একাধিক) খোলা; multi-image canvas grid layout (প্রতি row-তে ৪টা)
- Grid auto-detection (timing mark / corner marker; DPI-নিরপেক্ষ, আপেক্ষিক)
- Click করে element বসানো: Rotation Top/Bottom, OMR No, hasSaq, Barcode, QR Code,
  Roll No, Reg. Number, MCQ Answer, SAQ Answer, Question Quality, Custom, Virtual
- MCQ: প্রশ্ন সংখ্যা + option টাইপ করে সেট
- Element টেনে সরানো, handle দিয়ে resize, arrow-key nudge (0.1 / 0.5 cell),
  double-click-এ direction x↔y
- Generate XML (copy / save) — reference config-এর canonical order অনুযায়ী

### Editing
- Multi-select (Ctrl/Shift+click) → একসাথে move / nudge / remove
- Undo (Ctrl+Z, শেষ ১০০ ধাপ) ও Reset (confirm সহ)
- Element hide/unhide checkbox (XML-এ থাকে, শুধু canvas-এ লুকায়)
- ▲▼ দিয়ে element order (= XML output order)
- SAQ jোড়া-সহ Duplicate (পরের নম্বর, বাকিগুলো shift) + SAQ no input (swap সহ)
- Page-to-page "Copy ALL elements" (SAQ নম্বর continue)
- PropertyGrid-এ প্রতিটা attribute-এর সূক্ষ্ম এডিট (collapsible)

### Grid নির্ভুলতা ও Orientation
- Deskew + Auto Skew (বাঁকা scan সোজা করা)
- Rotate 180° — turned view, কিন্তু element mapping অবিকৃত (একই কাগজে বসে)
- Auto Rotate — XML লোডে Top/Bottom rotation marker-এ কালি দেখে orientation ঠিক
- Negative y (barcode/qrCode grid-এর উপরে, 0 ছাড়া: …, -2, -1, 1, 2, …)
- Grid line marker-এর ভেতরেই সীমাবদ্ধ (বাইরে ঘর/ফ্রেম আঁকে না)

### Open XML (config এডিট)
- পুরনো config XML designer-এ খুলে এডিট
- barcode/qrCode design-level ও page-level — দুই জায়গা থেকেই পড়ে
- virtual omrInfo (`<omrInfo isVirtual>`) round-trip

### Check OMR (mapping যাচাই)
- বর্তমান image + XML দিয়েই খোলে ও সাথে সাথে পড়ে
- magenta reading area, সবুজ detected mark overlay
- Barcode/QR decode (zxing.dll থাকলে; না থাকলে crash করে না — বার্তা দেখায়)
- প্রতি field-এ black% vs দরকারি%
- Row Data (roll | reg | সব answer এক লাইনে); value copy (double-click / Ctrl+C / menu)
- Auto-rotate সহ, canvas-এ double-click-এ নতুন image

### UI
- Icon toolbar (code-এ আঁকা, বাইরের file ছাড়া)
- প্রতি control-এ hover tooltip
- Wheel zoom / drag pan / Fit / multi-sheet zoom

### পরিচিত সীমাবদ্ধতা
- Check OMR হলো *mapping যাচাইয়ের* টুল — চূড়ান্ত grading commercial
  OMR Checker (OslOmr.Core) দিয়েই।
- নতুন mapping শুরুর সময় (config নেই) Auto Rotate কাজ করে না — "Rotate 180°" হাতে ব্যবহার করুন।

---

<!--
পরের release-এর টেমপ্লেট:

## [1.1.0] — YYYY-MM-DD
### Added
- ...
### Fixed
- ...
### Changed
- ...
-->
