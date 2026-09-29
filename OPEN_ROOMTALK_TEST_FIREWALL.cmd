@echo off
chcp 65001 >nul
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Vui long nhap chuot phai file nay va chon Run as administrator.
  pause
  exit /b 1
)

echo Dang mo cong TCP 5000 cho RoomTalk tren mang Private...
netsh advfirewall firewall delete rule name="RoomTalk Test TCP 5000" >nul 2>&1
netsh advfirewall firewall add rule name="RoomTalk Test TCP 5000" dir=in action=allow protocol=TCP localport=5000 profile=private

echo.
echo Da hoan tat. Neu ban dung cong khac 5000, hay sua localport trong file nay.
pause
