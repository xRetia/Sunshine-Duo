@echo off
pushd %~dp0

set "CERTUTIL=certutil"
where certutil >nul 2>&1 || set "CERTUTIL=%SystemRoot%\System32\certutil.exe"

echo ================
echo Installing cert for the SudoVDA driver...

%CERTUTIL% -addstore -f root "sudovda.cer" || exit /b 1
%CERTUTIL% -addstore -f TrustedPublisher "sudovda.cer" || exit /b 1

echo ================
echo Removing the old driver... It's OK to show an error if you're installing the driver for the first time.

nefconc.exe --remove-device-node --hardware-id root\sudomaker\sudovda --class-guid "4D36E968-E325-11CE-BFC1-08002BE10318"

echo ================
echo Installing the new driver...

nefconc.exe --create-device-node --class-name Display --class-guid "4D36E968-E325-11CE-BFC1-08002BE10318" --hardware-id root\sudomaker\sudovda || exit /b 2
nefconc.exe --install-driver --inf-path "SudoVDA.inf" || exit /b 3

echo ================
echo Done!

popd
