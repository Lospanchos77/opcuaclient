@echo off
setlocal

set "MA_DATE=%DATE%"
git add .
git commit -m "Commit %MA_DATE%"
git push