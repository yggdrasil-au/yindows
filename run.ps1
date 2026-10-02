
./build.ps1
if ($LASTEXITCODE -eq 0) {
	cosmos run --disk .\Fat.C.img --disk .\Fat.D.img
}

