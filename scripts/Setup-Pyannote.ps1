param(
    [string] $PythonExecutable = "python",
    [string] $VenvPath = "$env:APPDATA\LaptopOutputRecorder\pyannote-venv",
    [switch] $Cuda
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$requirementsPath = Join-Path $projectRoot "python\requirements.txt"

if (-not (Test-Path $requirementsPath)) {
    throw "Could not find $requirementsPath"
}

if (Test-Path $VenvPath) {
    Write-Host "Using existing pyannote virtual environment at $VenvPath"
}
else {
    Write-Host "Creating pyannote virtual environment at $VenvPath"
    & $PythonExecutable -m venv $VenvPath
}

$venvPython = Join-Path $VenvPath "Scripts\python.exe"
if (-not (Test-Path $venvPython)) {
    throw "Could not find venv Python at $venvPython"
}

Write-Host "Upgrading pip"
& $venvPython -m pip install --upgrade pip

if ($Cuda) {
    Write-Host "Installing CUDA-enabled PyTorch packages"
    & $venvPython -m pip install --force-reinstall torch==2.11.0+cu128 torchaudio==2.11.0+cu128 --index-url https://download.pytorch.org/whl/cu128
}

Write-Host "Installing pyannote requirements"
& $venvPython -m pip install -r $requirementsPath

Write-Host ""
Write-Host "Done."
Write-Host "The recorder will auto-detect this Python environment:"
Write-Host $venvPython
Write-Host ""
Write-Host "You still need ffmpeg available on PATH and a Hugging Face token via HF_TOKEN or settings.json."
Write-Host "For Nvidia GPU support, rerun this script with -Cuda."
