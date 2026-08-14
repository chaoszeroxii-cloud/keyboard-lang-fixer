# Keyboard Language Fixer

> **English summary.** Typed a whole sentence before noticing the keyboard was on
> the wrong language? Select it and press **Win+Space** — the same key you
> already use to switch input language — and every character is re-mapped by its
> physical key position. With nothing selected, Win+Space behaves exactly as
> Windows always did.
>
> It is not hard-coded to any language pair: at startup it asks Windows what each
> physical key produces under every keyboard layout installed on the machine
> (`ToUnicodeEx` + `MapVirtualKeyEx`), so any two installed layouts work. Caps
> Lock is handled as its own dimension, because layouts implement it differently.
>
> Windows only, no dependencies — one PowerShell script using the Win32 API.
> Double-click `Install.cmd`; the rest of this README is in Thai.

---

แก้ข้อความที่พิมพ์ผิดภาษา (ลืมสลับภาษา) ด้วย **Win+Space** ปุ่มเดิมที่ใช้สลับภาษาอยู่แล้ว

| สถานการณ์ | กด Win+Space แล้วได้อะไร |
|---|---|
| ไม่ได้ลากคลุมอะไร | Windows สลับภาษาตามปกติ **เหมือนเดิมทุกอย่าง** |
| ลากคลุมข้อความไว้ | สลับภาษา **และ** แปลงข้อความที่คลุมไว้ให้ด้วย |

```
l;ylfu   →  สวัสดี      (พิมพ์ไทยตอนโหมด EN)
้ำสสน    →  hello       (พิมพ์อังกฤษตอนโหมด TH)
็ำสสน    →  Hello       (shift ก็ตามไปด้วย)
```

## ไม่ได้ผูกกับไทย/อังกฤษ

ตอนเริ่มโปรแกรม มันจะ**ถาม Windows** ว่าปุ่มแต่ละตำแหน่งบนคีย์บอร์ด ในแต่ละผังภาษาที่ลงไว้ในเครื่อง ให้ตัวอักษรอะไร (`ToUnicodeEx` + `MapVirtualKeyEx`) แล้วสร้างตารางแปลงจากของจริง — เพิ่มภาษาอะไรใน Windows ก็แปลงภาษานั้นได้เลย ไม่ต้องแก้โค้ด (รัสเซีย เยอรมัน QWERTZ, ฝรั่งเศส AZERTY, ฮีบรู ฯลฯ)

ดูว่าเครื่องนี้แปลงอะไรได้บ้าง:

```powershell
powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -ListLayouts
```

**ทิศทางการแปลงไม่ต้องเลือก** — มันให้คะแนนแต่ละผังจาก "ตัวอักษรที่มีแต่ผังนี้เท่านั้นที่พิมพ์ได้" ผังที่ได้คะแนนสูงสุดคือผังที่ข้อความถูกพิมพ์ด้วย ที่เหลือคือปลายทาง ถ้าข้อความมีแต่ตัวที่ทุกผังพิมพ์ได้ (เช่น `-/-/`) มันจะ **ไม่เดา** — ปล่อยไว้เฉย ๆ แล้วส่งเสียงเตือน

ถ้าเครื่องลงไว้ผังเดียว จะถอยไปใช้ตาราง Kedmanee ที่ฝังมาในสคริปต์แทน

## Caps Lock

รองรับแล้ว และไม่ได้ใช้วิธีเดา — เพราะแต่ละผังตีความ Caps Lock **ไม่เหมือนกัน**:

| | ปุ่มตัวอักษร | ปุ่มตัวเลข/สัญลักษณ์ |
|---|---|---|
| อังกฤษ (US) | สลับเคส | **ไม่มีผล** |
| ไทย (Kedmanee) | สลับชั้น shift | **สลับชั้น shift ด้วย** |

โปรแกรมจึงถาม Windows แยกทั้ง 2 สถานะ (เปิด/ปิด Caps) ตอนสร้างตาราง แล้วตอนแปลงจะอ่านสถานะ Caps Lock ปัจจุบันมาใช้ — เพราะข้อความเพิ่งพิมพ์ไปเมื่อกี้ สถานะตอนนี้คือหลักฐานที่ดีที่สุด

ผลคือตอนเปิด Caps Lock: `1` → `+` (ไม่ใช่ `ๅ`) ซึ่งเป็นเคสที่เวอร์ชันก่อนหน้าทำผิด

---

## ติดตั้ง

ดับเบิลคลิก **`Install.cmd`** → กด OK จบ

มันจะ:
1. ก๊อปตัวโปรแกรมไป `%LOCALAPPDATA%\KeyboardLangFixer` (จะได้ไม่พังเวลาลบ/ย้ายโฟลเดอร์ต้นทาง)
2. ตั้งให้เปิดเองทุกครั้งที่ล็อกอิน
3. เปิดใช้งานทันที

ไม่ต้องใช้สิทธิ์ admin ไม่แตะ registry ไม่เขียนอะไรนอก user profile

**เอาไปเครื่องอื่น**: ก๊อปโฟลเดอร์นี้ไป (USB / zip / OneDrive) → ดับเบิลคลิก `Install.cmd` → OK

## ถอนการติดตั้ง

ดับเบิลคลิก **`Uninstall.cmd`** (อยู่ได้ทั้งในโฟลเดอร์ต้นทางและใน `%LOCALAPPDATA%\KeyboardLangFixer`)

มันลบทุกอย่างที่โปรแกรมสร้างขึ้น:

| ของที่โปรแกรมสร้าง | ถูกลบมั้ย |
|---|---|
| โปรเซสที่รันอยู่ | ปิด |
| shortcut ในโฟลเดอร์ Startup | ลบ |
| `%LOCALAPPDATA%\KeyboardLangFixer` (รวม `settings.json`) | ลบทั้งโฟลเดอร์ |
| registry | **ไม่เคยเขียนตั้งแต่แรก** |

สิ่งเดียวที่ไม่ลบคือ**โฟลเดอร์ต้นทางที่คุณกด Install.cmd** — เพราะนั่นคือไฟล์ของคุณเอง ลบเองได้เลย
(มีเทสต์ `test\install-test.ps1` ที่ถอนแล้วไล่กวาดหา Startup / AppData / registry Run key ว่าไม่เหลืออะไรจริง)

## ปิดโปรแกรม

คลิกขวาที่ไอคอนใน tray → **Exit** หรือกด **Ctrl+Alt+Shift+X**

เปิดซ้ำไม่ได้โดยตั้งใจ — ตัวที่สองจะขึ้นข้อความว่ามีตัวเดิมรันอยู่แล้วแล้วปิดตัวเอง (ถ้าปล่อยให้รันสองตัว ทั้งคู่จะแปลง selection เดียวกันคนละรอบ ผลคือได้ข้อความเดิมกลับมา)

---

## เปลี่ยนไอคอน

ไอคอนคือไฟล์ `icon.ico` ในโฟลเดอร์นี้ เอาไฟล์ใหม่มาทับได้เลย ถ้าไฟล์หายจะกลับไปใช้ไอคอน `ก` ที่วาดเองอัตโนมัติ

มีแต่ PNG ก็แปลงได้:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Convert-PngToIco.ps1 `
    -Source "C:\path\to\logo.png" -Destination icon.ico
```

ตัวแปลงจะครอปเอาเฉพาะส่วนที่ไม่โปร่งใส ทำให้เป็นสี่เหลี่ยมจัตุรัส แล้วเขียนเป็น .ico หลายขนาด (16/20/24/32/48/64/128) แบบ 32-bit มีอัลฟา
หลังเปลี่ยนไฟล์ต้องปิดแล้วเปิดโปรแกรมใหม่ และถ้าติดตั้ง auto-start ไว้ ให้รัน `Install-Startup.ps1` ซ้ำเพื่ออัปเดตไอคอนของ shortcut ด้วย

---

## เอาไปใช้เครื่องอื่น

ก๊อปทั้งโฟลเดอร์ไปวาง แล้วดับเบิลคลิก `Start-Hidden.vbs` — ใช่ครับ มันจะเฝ้า Win+Space เหมือนกัน ไม่ต้องติดตั้งอะไรเพิ่ม (ใช้ PowerShell กับ .NET ที่มากับ Windows อยู่แล้ว)

สิ่งที่ต้องเช็คบนเครื่องปลายทาง:

| เรื่อง | รายละเอียด |
|---|---|
| ภาษาไทยในระบบ | ต้องเพิ่ม keyboard layout ไทย (Kedmanee) ไว้แล้ว ไม่งั้นสลับภาษาไม่ได้ (ตัวแปลงข้อความยังทำงาน) |
| ปุ่มสลับภาษาของเครื่องนั้น | ดูด้วย `-ListLayouts` (ดูหัวข้อล่าง) |
| จำนวน layout | ถ้ามีเกิน 2 ภาษา Win+Space จะ **วนไปเรื่อย ๆ** ไม่ใช่สลับไปมา — แต่ตอนที่แปลงข้อความ สคริปต์จะบังคับภาษาให้ตรงกับผลลัพธ์อยู่แล้ว |
| Execution policy | `Start-Hidden.vbs` ใส่ `-ExecutionPolicy Bypass` ให้แล้ว ไม่ต้องไปแก้ policy ของเครื่อง |
| auto-start | ต้องรัน `Install-Startup.ps1` บนเครื่องนั้นอีกที (shortcut ชี้ไปที่ path ของโฟลเดอร์) |
| path | ไม่ต้องห่วง — `Install.cmd` ก๊อปเข้า `%LOCALAPPDATA%` ให้แล้ว ย้าย/ลบโฟลเดอร์ต้นทางได้ |

### ปุ่มสลับภาษา default ของ Windows ในเครื่องอื่น

Windows มีปุ่มสลับภาษา 2 แบบ และ**ต่างกันได้ในแต่ละเครื่อง**:

| ปุ่ม | ตั้งค่าได้มั้ย |
|---|---|
| **Win+Space** | **ไม่ได้ — ติดมากับ Windows 8 ขึ้นไปทุกเครื่อง ปิดไม่ได้จากหน้า Settings ปกติ** |
| Alt+Shift (ซ้าย) / Ctrl+Shift / `` ` `` | ได้ อยู่ที่ Settings → Time & language → Typing → Advanced keyboard settings → Input language hot keys (เก็บใน `HKCU\Keyboard Layout\Toggle`) |

เพราะงั้น**ค่า default `Win+Space` ของโปรแกรมนี้ใช้ได้ทุกเครื่อง** ไม่ว่าเครื่องนั้นจะตั้ง Alt+Shift ไว้หรือไม่ — และถ้าเครื่องนั้นเปิด Alt+Shift อยู่ ก็ไม่ชนกัน ใช้ Alt+Shift สลับภาษาเฉย ๆ ต่อไปได้ แล้วใช้ Win+Space ตอนอยากแปลงข้อความ

อยากรู้ว่าเครื่องไหนตั้งอะไรไว้:

```powershell
powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -ListLayouts
```

จะบอกทั้งผังภาษาที่แปลงได้ และปุ่มสลับภาษาที่เครื่องนั้นใช้อยู่

---

## เปลี่ยนปุ่มลัด

**คลิกขวาที่ไอคอนใน tray → Change hotkey…** (หรือดับเบิลคลิก `Settings.cmd`)

ติ๊ก Ctrl / Alt / Shift / Win แล้วเลือกปุ่ม → Save เปลี่ยนผลทันทีไม่ต้องรีสตาร์ท

ค่าที่เลือกเก็บใน `settings.json` ข้างตัวโปรแกรม → **ปิดเปิดเครื่องแล้วยังจำได้** และติดตั้งทับก็ไม่ทับค่านี้

> ต้องมีปุ่มจริงอย่างน้อย 1 ปุ่มเสมอ — ตั้งเป็น modifier ล้วน ๆ อย่าง `Alt+Shift` ไม่ได้

สั่งจาก command line ก็ได้ (ชนะค่าที่บันทึกไว้ ใช้เทสต์สะดวก):

```powershell
powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -Hotkey "Ctrl+Alt+X"
```

ชื่อปุ่มใช้ตาม `System.Windows.Forms.Keys` เช่น `Space`, `X`, `F9`, `Pause`

ออปชันอื่น:

| ออปชัน | ผล |
|---|---|
| `-NoLangSwitch` | แปลงข้อความอย่างเดียว ไม่ยุ่งกับภาษา input |
| `-NoTrayIcon` | ไม่ต้องมีไอคอนใน tray |
| `-LogPath log.txt` | เขียน log ทุกครั้งที่กดปุ่ม (ไว้ดูตอนมันไม่ทำงาน) |
| `-ListLayouts` | บอกว่าเครื่องนี้แปลงระหว่างผังภาษาอะไรได้บ้าง แล้วจบ |
| `-SelfTest` | เช็คตารางแมปคีย์ + เทียบผังที่ probe ได้กับตารางที่ฝังไว้ แล้วจบ |
| `-Mode Hotkey` | ยึดปุ่มนั้นไว้คนเดียว (ห้ามใช้กับ Win+Space) |

---

## มันทำงานยังไง

Win+Space เป็นปุ่มของ Windows shell เอง `RegisterHotKey` แย่งมาไม่ได้ สคริปต์จึงใช้ **low-level keyboard hook แบบไม่กลืนคีย์** — Windows ยังสลับภาษาเองตามปกติทุกครั้ง สคริปต์แค่รู้ว่ามีการกด แล้วทำงานเพิ่มทีหลัง

พอถูก trigger:

1. รอจนปล่อยปุ่ม Win ก่อน (ไม่งั้นคีย์ที่ยิงต่อจะติด modifier ไปด้วย)
2. ยิง **Ctrl+Insert** เพื่อก๊อป แล้วดูว่า clipboard sequence number ขยับมั้ย
   - ไม่ขยับ = ไม่ได้คลุมอะไร → **จบตรงนี้ ไม่แตะ clipboard เลย**
   - ใช้ Ctrl+Insert ไม่ใช่ Ctrl+C เพราะถ้าอยู่ในหน้าต่าง console Ctrl+C = สั่งหยุดโปรแกรมที่รันอยู่ (Ctrl+C เป็นแค่ตัวสำรอง และไม่ยิงในหน้าต่าง console)
3. หาว่าข้อความถูกพิมพ์ด้วยผังไหน แล้วแปลงทีละตัวตามตำแหน่งปุ่มจริง (96 ปุ่ม รวมชั้น shift)
4. เช็คว่าเขียน clipboard สำเร็จจริงก่อนวาง (ถ้าเขียนไม่ติดแล้ววาง จะกลายเป็นลบข้อความที่คลุมไว้ทิ้ง)
5. วางทับ แล้วตั้งภาษา input ให้ตรงกับผลลัพธ์ **พร้อมตรวจซ้ำว่าเปลี่ยนติดจริง** (ตัวสลับภาษาของ Windows commit ทีหลังบ้าง ถ้าไม่ตรวจจะโดนมันย้อนกลับ) แล้วคืน clipboard เดิม

ตาราง Kedmanee สำรองอยู่ใน `KeyboardLangFixer.ps1` ตัวแปร `$EnRows` / `$ThRows` เขียนเป็นเลข Unicode ทั้งหมด จะได้ไม่เพี้ยนเวลาโดนบันทึกผิด encoding — และ `-SelfTest` จะเอาผังไทยที่ probe จาก Windows มาเทียบกับตารางนี้ทั้ง 96 ปุ่ม เป็นการพิสูจน์ว่าวิธี probe เชื่อถือได้

---

## ข้อจำกัดที่ควรรู้

- **คลิปบอร์ด**: ถ้าไม่ได้คลุมอะไร คลิปบอร์ดไม่ถูกแตะเลย แต่ตอนที่แปลงจริง จะคืนได้เฉพาะ *ข้อความ* — ถ้าก่อนหน้านั้นก๊อปรูปหรือไฟล์ไว้ อันนั้นหาย
- **แอปที่ไม่รับ Ctrl+Insert**: มีตัวสำรองเป็น Ctrl+C ให้แล้ว ยกเว้นในหน้าต่าง console ที่ไม่ยิงให้โดยตั้งใจ
- **แอปสิทธิ์ admin**: ถ้าหน้าต่างที่โฟกัสรันแบบ elevated แต่สคริปต์ไม่ได้ ตัว hook จะไม่เห็นคีย์ (กฎ UIPI ของ Windows) — ต้องรันสคริปต์แบบ admin ด้วย
- **ข้อความปนสองภาษา**: ตัดสินทิศทางจากทั้งก้อน ถ้าคลุมทั้งประโยคที่มีทั้งไทยและอังกฤษปนกัน ผลจะไม่ใช่อย่างที่ต้องการ — คลุมเฉพาะส่วนที่พิมพ์ผิด
- **ผังไทยที่รองรับ** คือผังที่ลงไว้ใน Windows จริง ๆ (ปกติคือ Kedmanee) ถ้าตั้งเป็นปัตตะโชติ มันก็จะใช้ปัตตะโชติตามนั้น
- **ลงไว้เกิน 2 ภาษา**: ต้นทางเดาจากตัวข้อความได้ แต่ปลายทางจะเลือกภาษาที่ Windows เพิ่งสลับไป ถ้าไม่ใช่อันที่ต้องการก็กด Win+Space ซ้ำเพื่อวนต่อ
- **AltGr / ชั้นที่ 3**: อ่านเฉพาะปุ่มเปล่ากับ Shift ยังไม่รองรับตัวที่ต้องกด AltGr (สำคัญกับบางผังยุโรป)

---

## เทสต์

```powershell
# ตารางแมปคีย์ (ไม่แตะหน้าจอ)
powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -SelfTest

# ของจริง: เปิดหน้าต่างทดสอบแล้วกด Win+Space จริง (อย่าแตะคีย์บอร์ดตอนรัน)
powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\e2e-test.ps1

# tray icon + กันเปิดซ้ำ + ปุ่มออก
powershell -NoProfile -ExecutionPolicy Bypass -File test\tray-quit-test.ps1

# หน้าตั้งค่าคีย์ลัดเปิดขึ้นจริง (เซฟภาพไว้ดูด้วย)
powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\dialog-shot.ps1

# ติดตั้ง -> ค่าคีย์ลัดอยู่ข้ามการรีสตาร์ท -> ถอนแล้วกวาดหาของตกค้าง -> ติดตั้งกลับ
powershell -NoProfile -ExecutionPolicy Bypass -File test\install-test.ps1
```

> เทสต์จะปิดโปรแกรมที่รันอยู่ก่อน (เพราะ mutex กันเปิดซ้ำ) แล้วเปิดกลับให้ตอนจบ
