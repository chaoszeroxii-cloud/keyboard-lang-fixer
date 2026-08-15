# Keyboard Language Fixer

**[⬇ ดาวน์โหลดตัวที่ build แล้ว (Releases)](https://github.com/NatthananSky/keyboard-lang-fixer/releases/latest)** — แตกไฟล์ → ดับเบิลคลิก `Install.cmd` → OK

> **English summary.** Typed a whole word before noticing the keyboard was on the
> wrong language? Press **Ctrl+Alt+Space** and the last word is re-mapped by
> its physical key position. Select text first to fix exactly that instead.
>
> Win+Space is left entirely alone - the program does not even hook it - so the
> language switch keeps working exactly as before.
> It is not hard-coded to any language pair: at startup it asks Windows what each
> physical key produces under every keyboard layout installed on the machine
> (`ToUnicodeEx` + `MapVirtualKeyEx`), so any two installed layouts work. Caps
> Lock is handled as its own dimension, because layouts implement it differently.
>
> Press it again within a few seconds to put the original text back, exactly as
> it was. Windows'' own spell checker decides where a mistyped run ends, so
> several wrong words convert at once without touching the correct English in
> front of them, and named programs can be ignored entirely.
>
> Windows only, one 179 KB executable, no dependencies and no runtime to install:
> it builds with the C# compiler that ships with Windows. Double-click
> `Install.cmd`. The rest of this README is in Thai.

---

แก้ข้อความที่พิมพ์ผิดภาษา (ลืมสลับภาษา) ด้วย **`Ctrl+Alt+Space`**

| สถานการณ์ | กด Ctrl+Alt+Space แล้วได้อะไร |
|---|---|
| **ไม่ได้ลากคลุม** | **แก้คำสุดท้ายที่พิมพ์ให้เลย** + สลับภาษาให้ตรงผลลัพธ์ |
| ลากคลุมข้อความไว้ | แปลงเฉพาะที่คลุม + สลับภาษา |
| ไม่มีอะไรให้แก้ | ไม่ทำอะไร ไม่แตะคลิปบอร์ด |

> **`Win+Space` ไม่ถูกแตะเลย** — โปรแกรมไม่ติดตั้ง keyboard hook กับมันด้วยซ้ำ ปุ่มสลับภาษาเดิมของคุณทำงานเหมือนเดิม 100%
>
> เวอร์ชันแรกผูกกับ Win+Space แล้ว**ใช้ไม่ได้จริง** ด้วยเหตุผลสองข้อที่แก้ไม่ตก: (1) โปรแกรมแยกไม่ออกว่า "ฉันแค่สลับภาษา" กับ "แก้คำที่เพิ่งพิมพ์" ทำให้ไปทับข้อความที่พิมพ์ถูกอยู่แล้ว (2) ถ้าแยกด้วยการนับจำนวนครั้งที่กด ต้องพึ่ง low-level keyboard hook ซึ่ง Windows หยุดเรียกตอนโปรแกรมกำลังทำงาน และถอดทิ้งเงียบ ๆ ถ้า callback ค้างนาน — **วัดได้ว่าการกดครั้งที่สองส่งถึงราว 70% เท่านั้น** คีย์ลัดของตัวเองที่ยึดด้วย `RegisterHotKey` ระบบส่งให้ครบทุกครั้ง

```
l;ylfu   →  สวัสดี      (พิมพ์ไทยตอนโหมด EN)
้ำสสน    →  hello       (พิมพ์อังกฤษตอนโหมด TH)
็ำสสน    →  Hello       (shift ก็ตามไปด้วย)
```

---

## Smart Selection — ไม่ต้องแตะเมาส์

พิมพ์ผิดปุ๊บ กด Ctrl+Alt+Space ทีเดียว เปลี่ยนให้เลย ไม่ต้องลากคลุม

```
Please read l;ylfu c9j   →   Please read สวัสดี แต่
            └── แปลง ──┘        └ คำอังกฤษจริงไม่โดนแตะ
```

**ปัญหาที่ต้องแก้**: ข้อความที่พิมพ์บนผังผิด **แยกไม่ออกจากข้อความที่ตั้งใจพิมพ์บนผังนั้น** — `Please read l;ylfu` มี 3 คำที่ "ดูเหมือนพิมพ์บนผัง EN" เท่ากันหมด ถ้าเดินย้อนกินคำผังเดียวกันไปเรื่อย ๆ `Please read` ที่ถูกอยู่แล้วจะโดนแปลงด้วย

**ตัวตัดสิน: spell checker ของ Windows** (`ISpellChecker`) ใช้เป็นสัญญาณ **"หยุด"** อย่างเดียว — เจอคำอังกฤษที่สะกดถูก = คนตั้งใจพิมพ์ = จบ run ตรงนั้น

วัดจริงก่อนใช้ 35 คำ (~0.3 ms/คำ) ได้ผลว่า:

| | ผล | ใช้ยังไง |
|---|---|---|
| คำอังกฤษจริง (`Please read hello the quick send file`) | ok **11/11** | **เชื่อได้ → ใช้เป็นตัวหยุด** |
| ศัพท์ช่าง (`github` `powershell` `getUserId` `src` `png`) | **สะกดผิด** | ⚠️ เชื่อไม่ได้ → "สะกดผิด" ห้ามใช้เป็นเหตุผลกินต่อ |
| คำที่มีตัวเลข/สัญลักษณ์ (`-v[86I` `c9j`) | ถูกข้ามไม่ตรวจ | run หยุดก่อนเวลา = แปลงไม่ครบ (พลาดฝั่งปลอดภัย) |

เพราะข้อ 2 จึงยัง**จำกัดจำนวนคำ** ไว้ที่ `MaxSmartWords` (ค่าเริ่มต้น 3) ต่อให้ dictionary ตัดสินพลาด ความเสียหายก็ถูกล้อมไว้ — และกดคีย์ลัดซ้ำเพื่อ undo ได้ทันที

ถ้าเครื่องไม่มี dictionary อังกฤษ โปรแกรมจะ**ลดเหลือ 1 คำอัตโนมัติ** ไม่เดามั่ว
ทิศไทย→อังกฤษไม่ต้องพึ่ง spell checker เลย (อักษรไทยเป็นภาษาอังกฤษที่ถูกต้องไม่ได้) และไทยเขียนติดกันไม่เว้นวรรค → ทั้งวลีคือ 1 token อยู่แล้ว

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

ถ้าอะไรไม่เข้าเงื่อนไข → คืน selection ให้เคอร์เซอร์กลับที่เดิมเป๊ะ **และไม่ส่งเสียงเตือน** (คีย์ลัดนี้เป็นของโปรแกรมเอง การกดโดยไม่มีอะไรให้แก้จึงควรเงียบ ไม่ใช่ดุ)

ปิดฟีเจอร์นี้ได้ที่เมนู tray → *Fix the last word when nothing is selected*

## Smart Undo — กดซ้ำเพื่อเอาคืน

แปลงผิดใจ? กด Ctrl+Alt+Space ซ้ำภายใน 5 วินาที ได้ข้อความเดิมกลับมา **ตรงทุกตัวอักษร** พร้อมคืนภาษา input ให้ด้วย

- ก่อนคืน มัน**ตรวจก่อนว่าข้อความตรงหน้าเคอร์เซอร์ยังเป็นตัวที่เพิ่งวางจริง** ถ้าคุณพิมพ์อะไรต่อไปแล้ว มันจะไม่ยุ่ง
- คืนได้ครั้งเดียวต่อการแปลง 1 ครั้ง กดครั้งที่สามคือแปลงใหม่ ไม่ใช่เด้งไปมา
- แปลงข้ามหลายบรรทัดจะไม่เสนอ undo (การเลือกคืนนับเป็นตัวอักษร ข้ามบรรทัดไม่ได้)

> การกดซ้ำ "แปลงกลับ" ทำได้อยู่แล้วเพราะตารางเป็น bijection — แต่นั่นคือ *แปลงใหม่* ซึ่งอาจได้ช่วงไม่เท่าเดิม อันนี้คือคืนไบต์เดิมจริง ๆ

ตั้ง `UndoWindowSeconds` เป็น `0` เพื่อปิด

## Ignore-list — เว้นบางโปรแกรมไปเลย

```json
"IgnoreApps": ["valorant.exe", "cs2.exe"]
```

โปรแกรมที่อยู่ในรายการนี้ กดคีย์ลัดแล้ว **ไม่เกิดอะไรขึ้นเลย** ไม่ก๊อป ไม่ส่งคีย์ ไม่แตะคลิปบอร์ด

> **เหตุผลคือความถูกต้อง ไม่ใช่ performance** — hook callback เทียบแค่ virtual-key code ตัวเดียวแล้ว return ทันที ไม่ได้ยิงคีย์อะไรระหว่างเล่นเกมอยู่แล้ว
> การเช็คชื่อโปรเซสจึงทำ **หลัง**คีย์ลัดตรงเท่านั้น ถ้าไปเช็คในทุกปุ่มที่กดตามสัญชาตญาณ จะกลายเป็นถามชื่อโปรเซสทุกครั้งที่พิมพ์ = ช้าลงจริง ๆ
>
> ใช้ได้เต็มที่ในโหมดปกติ (`RegisterHotKey`) — โปรแกรมไม่ทำอะไรเลยเมื่อโปรแกรมนั้นอยู่หน้าสุด แต่ OS ยึดปุ่มไว้แล้ว จึงคืนปุ่มให้เกมไม่ได้ ถ้าเกมผูกปุ่มเดียวกันให้เปลี่ยนคีย์ลัดแทน


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
  "Hotkey": "Ctrl+Alt+Space",
  "SmartSelection": true,
  "SwitchLanguage": true,
  "MaxSmartChars": 300,
  "MaxSmartWords": 3,
  "UseSpellCheck": true,
  "UndoWindowSeconds": 5,
  "IgnoreApps": []
}
```

| ค่า | ความหมาย |
|---|---|
| `MaxSmartWords` | Smart Selection กินย้อนได้สูงสุดกี่คำ (ลดเหลือ 1 อัตโนมัติถ้าไม่มี dictionary) |
| `MaxSmartChars` | เพดานตัวอักษร กันเคสสุดโต่ง |
| `UseSpellCheck` | ใช้ spell checker หาจุดจบของ run |
| `UndoWindowSeconds` | กดซ้ำภายในกี่วินาทีถึงจะเป็น undo (`0` = ปิด) |
| `IgnoreApps` | ชื่อ .exe ที่จะไม่ทำอะไรเลย |

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

คีย์ลัดของโปรแกรม (`Ctrl+Alt+Space` โดยค่าเริ่มต้น) ยึดด้วย `RegisterHotKey` — ระบบส่งให้ตรง ๆ ไม่ต้องมี keyboard hook เลย จึงไม่มีปัญหา hook หมดเวลา ไม่มีปัญหาปุ่มหาย และ `Win+Space` ยังเป็นของ Windows เต็ม ๆ

hook callback ต้องคืนค่าเร็วมาก ไม่งั้น Windows จะเลิกเรียก — จึงแค่ `PostMessage` ไปหาหน้าต่างของตัวเองแล้วคืนทันที ส่วนงานหนักไปทำใน message loop และ re-seat hook หลังแปลงเสร็จทุกครั้ง

ก๊อปด้วย **Ctrl+Insert** ไม่ใช่ Ctrl+C เพราะในหน้าต่าง console Ctrl+C = สั่งหยุดโปรแกรมที่รันอยู่ (Ctrl+C เป็นตัวสำรอง และไม่ยิงใน console เด็ดขาด)

ตั้งภาษาแล้ว **ตรวจซ้ำว่าเปลี่ยนติดจริง** เพราะ flyout ของ Windows commit การสลับของมันทีหลัง ถ้าไม่ตรวจจะโดนย้อนกลับ

---

## เอาไปใช้เครื่องอื่น

ก๊อปทั้งโฟลเดอร์ไปวาง แล้วดับเบิลคลิก `Install.cmd` — ใช้คีย์ลัดเดียวกันทุกเครื่อง

| เรื่อง | รายละเอียด |
|---|---|
| runtime | ไม่ต้องลง — exe เป็น .NET Framework ที่มีมากับ Windows ทุกเครื่อง |
| ภาษาไทยในระบบ | ต้องเพิ่ม keyboard layout ไทยไว้แล้ว ไม่งั้นสลับภาษาไม่ได้ (ตัวแปลงยังทำงาน) |
| ปุ่มสลับภาษาของเครื่องนั้น | ดูด้วย `--list-layouts` |
| จำนวน layout | ถ้ามีเกิน 2 ภาษา ปลายทางจะเลือกภาษาที่ Windows อยู่ตอนนั้น |
| path | ไม่ต้องห่วง — `Install.cmd` ก๊อปเข้า `%LOCALAPPDATA%` ให้แล้ว |

### ปุ่มสลับภาษา default ของ Windows ต่างกันได้ในแต่ละเครื่อง

| ปุ่ม | ตั้งค่าได้มั้ย |
|---|---|
| **Win+Space** | **ไม่ได้ — ติดมากับ Windows 8+ ทุกเครื่อง ปิดไม่ได้จากหน้า Settings** |
| Alt+Shift (ซ้าย) / Ctrl+Shift / `` ` `` | ได้ ที่ Settings → Time & language → Typing → Advanced keyboard settings (เก็บใน `HKCU\Keyboard Layout\Toggle`) |

โปรแกรมนี้**ไม่ยุ่งกับปุ่มพวกนี้เลย** ไม่ว่าเครื่องนั้นจะตั้งไว้แบบไหน — มันมีคีย์ลัดของตัวเองแยกต่างหาก

---

## ข้อจำกัดที่ควรรู้

- **Smart Selection พึ่ง spell checker อังกฤษ** — คำที่ dictionary ไม่รู้จัก (`github`, `getUserId`, ชื่อคน, ชื่อไฟล์) ถ้าอยู่ติดกับคำที่พิมพ์ผิด อาจโดนกินไปด้วย ถูกล้อมด้วย `MaxSmartWords` และกดซ้ำเพื่อ undo ได้
- **Smart Undo คืนได้ครั้งเดียวต่อการแปลง** และเฉพาะเมื่อข้อความตรงหน้าเคอร์เซอร์ยังไม่ถูกแก้
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

# ของจริง: เปิดหน้าต่างทดสอบแล้วกดคีย์ลัดจริง 34 asserts
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
