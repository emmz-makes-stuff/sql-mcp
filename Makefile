# sql-mcp — gate targets.
#
# Every gate prints its own exit code as `LABEL_EXIT:<n>` and exits with it, so a
# result can be quoted rather than inferred from output that may look clean.
# `gates` runs the whole set with -k, so one run reports every failure.

SHELL := /bin/bash

CHANGES := $(notdir $(patsubst %/,%,$(filter-out %/archive/,$(wildcard openspec/changes/*/))))

.PHONY: build test itest format validate changes gates deps

build:
	@dotnet build sql-mcp.slnx -c Release; code=$$?; echo "BUILD_EXIT:$$code"; exit $$code

test:
	@dotnet test --project tests/SqlMcp.Tests/SqlMcp.Tests.csproj -c Release; code=$$?; echo "TEST_EXIT:$$code"; exit $$code

# Needs a Docker daemon (SQL Server 2025 container via Testcontainers).
itest:
	@dotnet test --project tests/SqlMcp.IntegrationTests/SqlMcp.IntegrationTests.csproj -c Release; code=$$?; echo "ITEST_EXIT:$$code"; exit $$code

format:
	@dotnet format sql-mcp.slnx --verify-no-changes; code=$$?; echo "FORMAT_EXIT:$$code"; exit $$code

# Validates every active change. No active change is a failure, not a silent pass.
validate:
	@fail=0; \
	if [ -z "$(CHANGES)" ]; then \
		echo "no active change found under openspec/changes/"; fail=1; \
	else \
		for c in $(CHANGES); do openspec validate $$c --strict || fail=1; done; \
	fi; \
	echo "VALIDATE_EXIT:$$fail"; exit $$fail

changes:
	@echo "$(CHANGES)"

gates:
	@$(MAKE) --no-print-directory -k build test itest format validate; code=$$?; \
	echo "GATES_EXIT:$$code"; exit $$code

# Every tool and login in docs/dependencies.md — keep the two in step. Not part of
# `gates`: it checks the machine, not the code, and auth checks may need the network.
deps:
	@fail=0; \
	dotnet --list-sdks 2>/dev/null | grep -q '^10\.0\.[4-9]' || { echo "WRONG VERSION dotnet — need SDK 10.0.400+ (brew install --cask dotnet-sdk)"; fail=1; }; \
	git --version >/dev/null 2>&1 || { echo "MISSING git — brew install git"; fail=1; }; \
	openspec --version >/dev/null 2>&1 || { echo "MISSING openspec — npm i -g @fission-ai/openspec"; fail=1; }; \
	gh --version >/dev/null 2>&1 || { echo "MISSING gh — brew install gh"; fail=1; }; \
	gh auth status >/dev/null 2>&1 || { echo "NOT AUTHENTICATED gh — gh auth login"; fail=1; }; \
	docker --version >/dev/null 2>&1 || { echo "MISSING docker — brew install orbstack; orb config set rosetta true"; fail=1; }; \
	docker info >/dev/null 2>&1 || { echo "NOT RUNNING docker daemon — start OrbStack"; fail=1; }; \
	echo "DEPS_EXIT:$$fail"; exit $$fail
