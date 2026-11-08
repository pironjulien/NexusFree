# Security policy

NexusFree runs commands under `SYSTEM`. A way for any account other than the authorized one, or for a non-admin
process of another session, to get a command run, to read another request or its answer, or to change the
dispatcher, its configuration or the scheduled task, is a vulnerability.

Please report it privately through
[GitHub private vulnerability reporting](https://github.com/pironjulien/NexusFree/security/advisories/new), not in a
public issue. Include your Windows version, the account types involved and the steps.

## Model and limits

- **By design, the authorized account can act as `SYSTEM` without a prompt.** Any program that runs under that
  account, malicious or not, can do so too, the same way it could use an elevated prompt you leave open. NexusFree
  removes UAC as a confirmation step for that account; it is not a sandbox and does not judge commands. Install it
  only for an account that is already an administrator, on a PC you control.
- **Other accounts:** the spool (`%ProgramData%\NexusFree`) has a protected ACL (nothing inherited from
  `%ProgramData%`): only `SYSTEM`, Administrators and the authorized account can write requests or read answers, and
  only they can start the task. The dispatcher also refuses a request whose file is not owned by the authorized
  account, Administrators or `SYSTEM`.
- **Logs:** every request is recorded in `%ProgramData%\NexusFree\logs\elevation.log` by its id and exit code, never its
  command; the AI app's own transcript keeps the command, so do not pass secrets on the command line.
- **Updates:** `install.ps1 -Apply` runs `NexusFree.exe --setup-system` through an installed NexusFree, which replaces the dispatcher (`%ProgramFiles%\NexusFree\NexusFree.exe`) under `SYSTEM`; run it
  only from a copy you trust (a release whose files match its `SHA256SUMS.txt` and build attestation, or the
  repository).

## Disclosure

You will get a first answer within 3 days. A confirmed vulnerability is fixed in a new release, usually within 30
days, then disclosed in a GitHub security advisory that credits the reporter unless they prefer otherwise. Please
keep it private until that release is out.

Only the latest release is supported.
