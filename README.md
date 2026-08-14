# Keyboard Language Fixer

**[⬇ ดาวน์โหลดตัวที่ build แล้ว (Releases)](https://github.com/NatthananSky/keyboard-lang-fixer/releases/latest)** — แตกไฟล์ → ดับเบิลคลิก `Install.cmd` → OK

> **English summary.** Typed a whole word before noticing the keyboard was on the
> wrong language? Press **Win+Space** — the same key you already use to switch
> input language — and the last word is re-mapped by its physical key position.
> Select text first to fix exactly that instead. With nothing to fix, Win+Space
> behaves exactly as Windows always did.
>
> It is not hard-coded to any language pair: at startup it asks Windows what each
> physical key produces under every keyboard layout installed on the machine
> (`ToUnicodeEx` + `MapVirtualKeyEx`), so any two installed layouts work. Caps
> Lock is handled as its own dimension, because layouts implement it differently.
>
> Windows only, one 166 KB executable, no dependencies and no runtime to install:
> it builds with the C# compiler that ships with Windows. Double-click
> `Install.cmd`. The rest of this README is in Thai.

---

แก้ข้อความที่พิมพ์ผิดภาษา (ลืมสลับภาษา) ด้วย **Win+Space** ปุ่มเดิมที่ใช้สลับภาษาอยู่แล้ว

| สถานการณ์ | กด Win+Space แล้วได้อะไร |
|---|---|
| **ไม่ได้ลากคลุม** | **แก้คำสุดท้ายที่พิมพ์ให้เลย** + สลับภาษา |
| ลากคลุมข้อความไว้ | แปลงเฉพาะที่คลุม + สลับภาษา |
| ไม่มีอะไรให้แก้ (บรรทัดว่าง / ตัวที่กำกวม) | Windows สลับภาษาตามปกติ **เหมือนเดิมทุกอย่าง** |

```
l;ylfu   →  สวัสดี      (พิมพ์ไทยตอนโหมด EN)
้ำสสน    →  hello       (พิมพ์อังกฤษตอนโหมด TH)
็ำสสน    →  Hello       (shift ก็ตามไปด้วย)
```

---

## Smart Selection — ไม่ต้องแตะเมาส์

พิมพ์ผิดปุ๊บ กด Win+Space ทีเดียว คำสุดท้ายเปลี่ยนให้เลย ไม่ต้องลากคลุม

**ทำไมเอาแค่คำสุดท้ายคำเดียว** — ไม่ใช่ความขี้ขลาด แต่เป็นข้อจำกัดที่แก้ไม่ได้จริง ๆ:
ข้อความที่พิมพ์บนผังผิด **แยกไม่ออกจากข้อความที่ตั้งใจพิมพ์บนผังนั้น** ประโยค `Please read l;ylfu` มี 3 คำที่ "ดูเหมือนพิมพ์บนผัง EN" ทั้งหมด ถ้าเดินย้อนกินคำที่เป็นผังเดียวกันไปเรื่อย ๆ มันจะแปลง `Please read` ที่ถูกอยู่แล้วไปด้วย
(ทิศทางไทย→อังกฤษไม่มีปัญหานี้ เพราะอักษรไทยเป็นภาษาอังกฤษที่ถูกต้องไม่ได้ — แต่กฎที่ทำงานถูกแค่ทิศเดียวแย่กว่ากฎที่คาดเดาได้ทั้งสองทิศ)

**ภาษาไทยไม่เสียอะไรจากข้อจำกัดนี้** เพราะไทยเขียนติดกันไม่เว้นวรรค → วลีที่พิมพ์ผิดทั้งวลีคือ 1 token อยู่แล้ว

อยากให้กินหลายคำ ตั้ง `MaxSmartWords` ใน `settings.json`

### กลไก (จุดที่พลาดง่ายและถูกแก้ไปแล้ว)

1. `Shift+Home` **ปุ่มเดียว** → คลุมจากเคอร์เซอร์ถึงต้นบรรทัด ไม่มีแอปไหนตีความกำกวม และข้ามบรรทัดไม่ได้
2. ก๊อปมาอ่าน แล้วคำนวณว่าท้ายสุด **กี่ตัวอักษร** ที่ควรแปลง (ฟังก์ชันบริสุทธิ์ เทสต์ได้โดยไม่ต้องเปิดหน้าต่าง)
3. หุบ selection จากซ้ายด้วย `Shift+Right` → เหลือเฉพาะช่วงท้ายที่ต้องการ **แม่นระดับตัวอักษร**
4. ก๊อปยืนยันอีกครั้งว่าได้ช่วงตรงตามที่คำนวณ **ก่อน**จะเขียนทับอะไร
5. แปลง วาง สลับภาษา คืนคลิปบอร์ด

> **ไม่ใช้ `Ctrl+Shift+Left` นับคำ** เพราะนิยาม "คำ" ต่างกันในแต่ละแอป — `l;ylfu` เป็น 1 คำในบางแอปและ 3 คำในบางแอป (เครื่องหมายวรรคตอนตัดคำ) นับพลาดคือคลุมผิดช่วงแบบเงียบ ๆ
>
> **ปุ่มนำทางต้องใส่ `KEYEVENTF_EXTENDEDKEY`** ไม่ใส่แล้ว `keybd_event` จะ map ไปที่ปุ่ม numpad → ถ้า NumLock เปิด `Shift+Home` จะกลายเป็น `Shift+numpad7` ซึ่ง **ย้ายเคอร์เซอร์แต่ไม่คลุม** ผลคือฟีเจอร์ไม่ทำงาน แต่เคอร์เซอร์เด้งไปต้นบรรทัดให้ผู้ใช้เห็น
>
> **ยุบ selection ด้วย `Right` ไม่ได้** เพราะลูกศรขยับจาก *active end* ของ selection ซึ่งหลัง `Shift+Home` คือต้นบรรทัด ไม่ใช่ตำแหน่งเคอร์เซอร์เดิม

ถ้าอะไรไม่เข้าเงื่อนไข → คืน selection ให้เคอร์เซอร์กลับที่เดิมเป๊ะ **และไม่ส่งเสียงเตือน** (เพราะคนกด Win+Space เพื่อสลับภาษาเฉย ๆ เป็นส่วนใหญ่ เตือนทุกครั้งคือทรมาน)

ปิดฟีเจอร์นี้ได้ที่เมนู tray → *Fix the last word when nothing is selected*

---

## ไม่ได้ผูกกับไทย/อังกฤษ

ตอนเริ่มโปรแกรม มันจะ**ถาม Windows** ว่าปุ่มแต่ละตำแหน่ง ในแต่ละผังภาษาที่ลงไว้ ให้ตัวอักษรอะไร แล้วสร้างตารางแปลงจากของจริง — เพิ่มภาษาอะไรใน Windows ก็แปลงได้เลย ไม่ต้องแก้โค้ด (รัสเซีย เยอรมัน QWERTZ, ฝรั่งเศส AZERTY, ฮีบรู ฯลฯ)

```
KeyboardLangFixer.exe --list-layouts
```

**ทิศทางไม่ต้องเลือก** — ให้คะแนนแต่ละผังจาก "ตัวอักษรที่มีแต่ผังนี้เท่านั้นที่พิมพ์ได้" ถ้าข้อความมีแต่ตัวที่ทุกผังพิมพ์ได้ (เช่น `-/-/`) มันจะ **ไม่เดา**

ถ้าเครื่องลงไว้ผังเดียว จะถอยไปใช้ตาราง Kedmanee ที่ฝังมาในโปรแกรม

## Caps Lock

รองรับแล้ว และไม่ได้ใช้วิธีเดา เพราะแต่ละผังตีความ Caps Lock **ไม่เหมือนกัน**:

| | ปุ่มตัวอักษร | ปุ่มตัวเลข/สัญลักษณ์ |
|---|---|---|
| อังกฤษ (US) | สลับเคส | **ไม่มีผล** |
| ไทย (Kedmanee) | สลับชั้น shift | **สลับชั้น shift ด้วย** |

จึงถาม Windows แยกทั้ง 2 สถานะตอนสร้างตาราง แล้วตอนแปลงอ่านสถานะ Caps ปัจจุบันมาใช้
ผลคือเปิด Caps Lock แล้ว `1` → `+` (ไม่ใช่ `ๅ`) ซึ่งเป็นเคสที่เวอร์ชันไม่รู้จัก Caps ทำผิด

## คลิปบอร์ดไม่หาย

- **ไม่ได้ลากคลุมและไม่มีอะไรให้แก้** → ไม่แตะคลิปบอร์ดเลย (ใช้ `GetClipboardSequenceNumber` ตรวจว่ามีการก๊อปจริงมั้ย แทนการล้างแล้วดู)
- **เป็นข้อความ** → เก็บสตริงไว้คืน ทางเร็ว ไม่ copy อะไรเพิ่ม
- **เป็นรูป / ไฟล์ / rich content** → เก็บ `IDataObject` แบบ format-by-format ไว้คืน **จ่ายค่า copy เฉพาะตอนที่เจอของแบบนี้จริง** ไม่ใช่ทุกครั้ง

## ใช้ทรัพยากรเท่าไหร่

วัดจริงบน Windows 11 หลังรัน 28 นาที:

| | เวอร์ชัน PowerShell (เก่า) | exe (ปัจจุบัน) |
|---|---|---|
| private bytes | 93.9 MB | **23.6 MB** |
| working set | 118.7 MB | **32.8 MB** |
| CPU ตอน start | 0.98 s | **0.13 s** |
| CPU ตอน idle 20 วินาที | 0.000 s | **0.000 s** |
| handles | 546 | 278 |

ตอนไม่ทำอะไรมันนอนรออยู่ใน message loop — **ไม่มี timer ไม่มี polling** CPU เป็น 0 จริง

> ⚠️ **อย่าใช้ `EmptyWorkingSet` เพื่อให้เลขใน Task Manager สวย** — วัดแล้วมันลด working set ได้จริง (117 MB → 0 MB) แต่ commit ไม่ลด และเพจต้องถูกโหลดกลับ ซึ่งสำหรับ keyboard hook หมายถึงไปเกิดตอน**คุณกดปุ่มถัดไป** = แลกอาการพิมพ์สะดุดกับเลขสวยปลอม ๆ

---

## ติดตั้ง

ดับเบิลคลิก **`Install.cmd`** → กด OK จบ

1. คอมไพล์ exe ให้ถ้ายังไม่มี (ใช้คอมไพเลอร์ที่ติดมากับ Windows ไม่ต้องลงอะไร)
2. ก๊อปไป `%LOCALAPPDATA%\KeyboardLangFixer` (ลบ/ย้ายโฟลเดอร์ต้นทางได้)
3. ตั้งให้เปิดเองทุกครั้งที่ล็อกอิน
4. เปิดใช้งานทันที

ไม่ต้องใช้สิทธิ์ admin ไม่แตะ registry ไม่เขียนอะไรนอก user profile

**เอาไปเครื่องอื่น**: ก๊อปโฟลเดอร์ไป → `Install.cmd` → OK

## ถอนการติดตั้ง

ดับเบิลคลิก **`Uninstall.cmd`** ลบทุกอย่างที่โปรแกรมสร้าง:

| ของที่โปรแกรมสร้าง | ถูกลบมั้ย |
|---|---|
| โปรเซสที่รันอยู่ | ปิด |
| shortcut ในโฟลเดอร์ Startup | ลบ |
| `%LOCALAPPDATA%\KeyboardLangFixer` (รวม `settings.json`) | ลบทั้งโฟลเดอร์ |
| registry | **ไม่เคยเขียนตั้งแต่แรก** |

ไม่ลบอย่างเดียวคือโฟลเดอร์ต้นทางที่คุณกด Install.cmd — นั่นคือไฟล์ของคุณเอง
(`test\install-test.ps1` ถอนแล้วไล่กวาดหา Startup / AppData / registry Run key ว่าไม่เหลืออะไรจริง)

## ปิดโปรแกรม

คลิกขวาไอคอน tray → **Exit** หรือกด **Ctrl+Alt+Shift+X**
เปิดซ้ำไม่ได้โดยตั้งใจ — ตัวที่สองจะบอกว่ามีตัวเดิมอยู่แล้วแล้วปิดตัวเอง (ถ้าปล่อยให้รันสองตัว ทั้งคู่จะแปลง selection เดียวกันคนละรอบ = ได้ข้อความเดิมกลับมา)

---

## ตั้งค่า

**คลิกขวาไอคอน tray** มีให้ทั้งหมด:

| เมนู | ทำอะไร |
|---|---|
| Change hotkey... | เลือกคีย์ลัดใหม่ มีผลทันทีไม่ต้องรีสตาร์ท |
| Fix the last word when nothing is selected | เปิด/ปิด Smart Selection |
| Switch input language after converting | เปิด/ปิดการสลับภาษาหลังแปลง |
| Start with Windows | เปิด/ปิด auto-start |

ค่าเก็บใน `settings.json` ข้างตัวโปรแกรม → **ปิดเปิดเครื่องแล้วยังจำได้** และติดตั้งทับก็ไม่ทับไฟล์นี้

```json
{
  "Hotkey": "Win+Space",
  "SmartSelection": true,
  "SwitchLanguage": true,
  "MaxSmartChars": 300,
  "MaxSmartWords": 1
}
```

> คีย์ลัดต้องมีปุ่มจริงอย่างน้อย 1 ปุ่ม — ตั้งเป็น modifier ล้วน ๆ อย่าง `Alt+Shift` ไม่ได้

### command line

```
KeyboardLangFixer.exe                    รันใน tray (ค่าปกติ)
KeyboardLangFixer.exe --self-test        เช็คตารางแปลง + Smart Selection แล้วจบ
KeyboardLangFixer.exe --list-layouts     บอกผังที่แปลงได้ + ปุ่มสลับภาษาของเครื่องนี้
KeyboardLangFixer.exe --configure-hotkey เปิดหน้าเลือกคีย์ลัด

  --hotkey <combo>       ทับค่าใน settings.json
  --mode Auto|Hook|Hotkey
  --no-smart             ปิด Smart Selection
  --no-lang-switch       แปลงข้อความอย่างเดียว
  --no-tray              ไม่ต้องมีไอคอน tray
  --log <file>           เขียน log ทุกครั้งที่กดปุ่ม
  --install-startup / --uninstall-startup
```

---

## มันทำงานยังไง

Win+Space เป็นปุ่มของ Windows shell เอง `RegisterHotKey` แย่งมาไม่ได้ โปรแกรมจึงใช้ **low-level keyboard hook แบบไม่กลืนคีย์** — Windows ยังสลับภาษาเองตามปกติ โปรแกรมแค่รู้ว่ามีการกดแล้วทำงานเพิ่มทีหลัง (คีย์ลัดที่ไม่มี Win จะยึดด้วย `RegisterHotKey` ตามปกติ)

hook callback ต้องคืนค่าเร็วมาก ไม่งั้น Windows จะเลิกเรียก — จึงแค่ `PostMessage` ไปหาหน้าต่างของตัวเองแล้วคืนทันที ส่วนงานหนักไปทำใน message loop และ re-seat hook หลังแปลงเสร็จทุกครั้ง

ก๊อปด้วย **Ctrl+Insert** ไม่ใช่ Ctrl+C เพราะในหน้าต่าง console Ctrl+C = สั่งหยุดโปรแกรมที่รันอยู่ (Ctrl+C เป็นตัวสำรอง และไม่ยิงใน console เด็ดขาด)

ตั้งภาษาแล้ว **ตรวจซ้ำว่าเปลี่ยนติดจริง** เพราะ flyout ของ Windows commit การสลับของมันทีหลัง ถ้าไม่ตรวจจะโดนย้อนกลับ

---

## เอาไปใช้เครื่องอื่น

ก๊อปทั้งโฟลเดอร์ไปวาง แล้วดับเบิลคลิก `Install.cmd` — เฝ้า Win+Space เหมือนกัน

| เรื่อง | รายละเอียด |
|---|---|
| runtime | ไม่ต้องลง — exe เป็น .NET Framework ที่มีมากับ Windows ทุกเครื่อง |
| ภาษาไทยในระบบ | ต้องเพิ่ม keyboard layout ไทยไว้แล้ว ไม่งั้นสลับภาษาไม่ได้ (ตัวแปลงยังทำงาน) |
| ปุ่มสลับภาษาของเครื่องนั้น | ดูด้วย `--list-layouts` |
| จำนวน layout | ถ้ามีเกิน 2 ภาษา Win+Space จะวนไปเรื่อย ๆ ไม่ใช่สลับไปมา |
| path | ไม่ต้องห่วง — `Install.cmd` ก๊อปเข้า `%LOCALAPPDATA%` ให้แล้ว |

### ปุ่มสลับภาษา default ของ Windows ต่างกันได้ในแต่ละเครื่อง

| ปุ่ม | ตั้งค่าได้มั้ย |
|---|---|
| **Win+Space** | **ไม่ได้ — ติดมากับ Windows 8+ ทุกเครื่อง ปิดไม่ได้จากหน้า Settings** |
| Alt+Shift (ซ้าย) / Ctrl+Shift / `` ` `` | ได้ ที่ Settings → Time & language → Typing → Advanced keyboard settings (เก็บใน `HKCU\Keyboard Layout\Toggle`) |

เพราะงั้น **ค่า default `Win+Space` ใช้ได้ทุกเครื่อง** และถ้าเครื่องนั้นเปิด Alt+Shift อยู่ก็ไม่ชนกัน

---

## ข้อจำกัดที่ควรรู้

- **Smart Selection เอาแค่คำสุดท้าย** (เหตุผลอยู่ด้านบน) — คลุมเองถ้าต้องการหลายคำ
- **แอปที่ไม่รับ Ctrl+Insert**: มีตัวสำรองเป็น Ctrl+C ยกเว้นในหน้าต่าง console ที่ไม่ยิงให้โดยตั้งใจ
- **หน้าต่าง console**: Smart Selection ไม่ทำงาน (Shift+Home ใน terminal ไม่ได้หมายถึงคลุมข้อความ) — ลากคลุมเองยังใช้ได้
- **แอปสิทธิ์ admin**: ถ้าหน้าต่างที่โฟกัสรันแบบ elevated แต่โปรแกรมไม่ได้ hook จะไม่เห็นคีย์ (กฎ UIPI ของ Windows) — ต้องรันแบบ admin ด้วย
- **ข้อความปนสองภาษา**: ตัดสินทิศทางจากทั้งก้อน — คลุมเฉพาะส่วนที่พิมพ์ผิด
- **AltGr / ชั้นที่ 3**: อ่านเฉพาะปุ่มเปล่ากับ Shift ยังไม่รองรับตัวที่ต้องกด AltGr (สำคัญกับบางผังยุโรป)

---

## build เอง

```
build.cmd
```

ใช้ `csc.exe` ที่อยู่ใน `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` — ติดมากับ Windows ไม่ต้องลง SDK
โค้ดเป็น **C# 5** ทั้งหมด (ไม่มี `$""`, `?.`, `nameof`) เพราะคอมไพเลอร์ตัวนั้นเก่า — แลกกับการที่ใครก็ build ได้ทันที
`KeyboardLangFixer.exe` เป็น build artifact จึงไม่อยู่ใน git — Release แนบตัวที่ build แล้วไว้

## เทสต์

```powershell
# ตารางแปลง + Caps Lock + ตรรกะ Smart Selection (ไม่แตะหน้าจอ, 77 checks)
.\KeyboardLangFixer.exe --self-test

# ของจริง: เปิดหน้าต่างทดสอบแล้วกด Win+Space จริง 20 asserts
powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\e2e-test.ps1

# tray icon + กันเปิดซ้ำ + ปุ่มออก
powershell -NoProfile -ExecutionPolicy Bypass -File test\tray-quit-test.ps1

# หน้าเลือกคีย์ลัดเปิดขึ้นจริง (เซฟภาพไว้ดู)
powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\dialog-shot.ps1

# ติดตั้ง → ค่าอยู่ข้ามรีสตาร์ท → ถอนแล้วกวาดหาของตกค้าง → ติดตั้งกลับ
powershell -NoProfile -ExecutionPolicy Bypass -File test\install-test.ps1
```

e2e ครอบคลุม: Smart Selection ทั้งแบบไม่ลากคลุม / ประโยคยาวเว้นวรรคสามช่อง 12 คำ / วลีไทยติดกัน / ตัวกำกวมแล้วคืนเคอร์เซอร์ / บรรทัดว่าง, ลากคลุมทั้งบรรทัดและบางส่วน, shift layer, ภาษาปลายทางถูกฝั่ง, คลิปบอร์ด**ข้อความและรูปภาพ**รอด

> เทสต์จะปิดโปรแกรมที่รันอยู่ก่อน (mutex กันเปิดซ้ำ) แล้วเปิดกลับให้ตอนจบ

## เปลี่ยนไอคอน

ไอคอนคือ `icon.ico` ในโฟลเดอร์โปรแกรม เอาไฟล์ใหม่ทับได้เลย (ถ้าหายจะกลับไปวาดไอคอน `ก` เอง)
มีแต่ PNG ก็แปลงได้: `powershell -File tools\Convert-PngToIco.ps1 -Source logo.png -Destination icon.ico`
เปลี่ยนแล้ว build ใหม่ด้วย เพราะ exe ฝังไอคอนไว้เป็น icon ของไฟล์
