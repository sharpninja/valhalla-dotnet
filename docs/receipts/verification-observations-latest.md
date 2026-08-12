# Verification observations 20260812T215313Z (post Azure-out policy)

## Lab policy
- Azure: not used
- Hosts: PAYTON-LEGION2, PAYTON-DESKTOP
- Deploy: Octopus
- Origin: GitHub (no GitHub Actions)

## Host bar
- LEGION2: 16 vCPU / 23.37 GiB / ~1259 free disk -> fails formal 32/64/1024
- DESKTOP: ping True; WinRM/SSH/admin-share fail from LEGION2
- PBF: ready on E:

## Suites (prior receipts still valid)
- Generation.Tests D+R 380/380
- Gate filter 33/33

## MCP
- Marker file missing; done state not re-queried this turn
- Phase remains incomplete for formal L48 DoD

## F6
- Merge done; promo/CLI/done blocked on lab host bar or APPROVE
