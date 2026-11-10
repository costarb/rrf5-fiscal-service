# Runtime prerequisite check

Checked on **2026-08-04** on the current Windows development machine.

| Candidate | Required baseline | Locally detected | Execution status |
| --- | --- | --- | --- |
| .NET | SDK 10 LTS | SDK 8.0 and 9.0 preview | Scaffold only; blocked locally until SDK 10 or container is available |
| Java | JDK 25 LTS, Maven 3.9+ | JDK 19 | Scaffold only; blocked locally until JDK 25/toolchain is available |
| TypeScript | Node.js 20+ | Node.js 20.20.2, npm 11.13.0 | Runtime requirement met; dependencies still need installation |

Runtime availability does not influence the final score by itself. All candidates must pass the same fiscal and portability gates in a reproducible Linux environment.

