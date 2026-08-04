# Contributing

## Git Flow

- `main` represents released, approved work.
- `develop` integrates completed features.
- Create work from `develop` using `feature/<sequence>-<description>`.
- Open a pull request from the feature branch to `develop`; do not commit directly to `main` or `develop`.
- Promote releases from `develop` to `main` through a reviewed release branch or pull request.

Use focused commits and include the task identifier when useful. Before pushing, run the checks documented in the feature quickstart.

## Fiscal evidence

- Tax behavior must cite an official source and its checked date.
- Do not infer a tax rule from library behavior or a successful authorization alone.
- Record document version, applicability, environment and artifact hash when the official artifact is available.
- Mark missing evidence or external dependencies as blocked; never manufacture an approval or homologation result.
- Changes to accepted fiscal decisions require review by the responsible fiscal stakeholder.

## Security and fixtures

Follow `SECURITY.md`. Fixtures must be synthetic and declare that they are sanitized. Never attach a real certificate, CSC, customer XML or secret to commits, issues, pull requests or CI logs.

