# AI Workloads

AI Workloads is where everything that runs models or AI work shows up, whoever started it, and where avamux acts as
the admin: it can stop anything it can see (asking first when it did not start it), start and swap model servers,
and say why one failed. The page has four tabs: **Running**, **Profiles**, **Tasks** and **Models**.

## Running

Everything AI on this computer, and on other machines plugins reach:

- **Model servers**: llama.cpp servers and RPC servers, whisper.cpp, Ollama and its loaded models, LM Studio, vLLM,
  Kokoro…
- **Coding agents**: Claude Code and pi, wherever they run: in avamux terminals, in AI Chat, anywhere else.
- **AI containers**, running or stopped (Open WebUI, ComfyUI, SillyTavern, vLLM, CUDA build boxes…), which can be
  started and stopped from here.

Each row has its port, context, GPU and RAM use, how long it has run and who started it. Open a row for its details
and its log (a server's log, a start script's output, an AI run's thinking and reply), followed live.

## Profiles

A profile is a way to start a server.

| Kind | What it is |
|---|---|
| `llama.cpp` | A llama.cpp server avamux launches itself: the program, model, alias, host, port, context and any other arguments. |
| `script` | A start/stop script pair, found in the folders named by `ai.workloadScripts` (`start-*.sh` with its `stop-*.sh`). The script is the config: its `BIN`, `MODEL`, `PORT`, `CTX`, `ALIAS`, pid and log files are read from it and from the files it sources. |
| `command` | Any command that stays in the foreground, with a stop command and the address it answers on. |
| `rpc` | A remote llama.cpp RPC server: status, start, stop, restart and version commands (over ssh, for example). |
| `api` | A hosted API (openai, anthropic or ollama style). Best effort; the key stays in an environment variable. |

Every profile can also have a group (below), RPC servers it needs, leases it must get first, an idle time after which
avamux stops it (when avamux started it), how long to wait for it to be ready, and notes.

```sh
avamux ai workloads profile add my-model --kind llama.cpp --binary ~/llama.cpp/build/bin/llama-server \
  --model ~/models/my-model-Q4_K_M.gguf --alias my-model --port 8080 --ctx 32768 --arg --flash-attn --arg on
avamux ai workloads profile add whisper --kind command --command "whisper-server --port 8090" \
  --url http://127.0.0.1:8090 --group ""
avamux ai workloads folders --scripts ~/ai/scripts   # where start/stop scripts are found (--models for model files)
avamux ai workloads profiles                  # every profile; profile show|set|remove to manage one
```

A found script cannot be removed, only hidden (`profile set <profile> --hidden true`); `profile set` also changes its
name, start arguments, group, idle time, RPC servers, leases and notes.

## Swapping

Profiles in one group never run together. Servers on this computer are in `gpu` to begin with, so starting one first
stops whatever holds the GPU or its port, and says so first.

- It refuses while the server in the way is answering a request, unless you insist (`--force`). `--no-swap` refuses
  instead of stopping anything.
- It also names other programs holding GPU memory (a game, say), and warns when a server would be left with less than
  800 MiB of the card.
- `avamux ai workloads check <profile>` says what starting a profile would stop, and what else holds the GPU, without
  changing anything. `avamux ai workloads gpu` shows GPU memory and how much each process holds.

Servers avamux starts keep running when it quits. Closing the window asks whether to stop them (`ai workloads
quit-check` lists them; `ai workloads stop-started` stops them all).

## Stopping

Every workload is stopped its own way: a script profile by its stop script, a container (or distrobox) through
podman or docker, a process by signal.

A script profile's stop is checked. After the stop script, avamux makes sure the server really is gone: its process,
and nothing answering on its address. If the script reports success but the server is still there, avamux says so,
with the pid, and the row offers **End process** (SIGTERM, then SIGKILL). A process avamux may not signal, or one that
survives SIGKILL, is reported as not stopped, with the reason.

The row says "Stopping…" from the click on (a stop script can wait half a minute for the server to let go of the
GPU), and every action and its outcome is written to the app log.

## Why it failed

When a server does not come up, avamux reads the end of its log for the usual reasons and says them in plain words:

- out of GPU memory;
- the port is in use;
- an RPC server built from another llama.cpp commit;
- a missing library or model file;
- a model architecture the program does not know.

## Tasks

AI Chat replies and [AI Actions](ai-actions.md) in progress (stop them, or open the chat), recent runs and how they
ended, and jobs from queue plugins.

## Models

Model files on disk:

- **GGUF** files (split ones counted once), with what their header says: architecture, size, quantisation, context,
  layers, experts;
- **safetensors**, **ONNX** and **whisper.cpp** files.

Each shows which profiles use it, and a GGUF file has a button that makes a llama.cpp profile from it. Files are
searched for in `~/models`, `~/.ollama/models`, `~/.lmstudio/models`, `~/.cache/huggingface/hub`,
`~/.cache/lm-studio/models` and `~/.cache/llama.cpp` (those that exist), and in `ai.workloadModels`.
`avamux ai workloads folders` shows the list, and `--models` replaces it.

## For plugins

Plugins extend AI Workloads in two ways.

**Sources and leases**, through [`IPluginContext`](plugins.md):

- `AddWorkloadSource(IWorkloadSource)`: another machine's queue, its models and jobs, shown beside the local ones.
- `AddLeaseProvider(IWorkloadLeaseProvider)`: a profile, or an RPC server it uses, can need a lease from another
  machine's queue before it starts. The lease is released when the server stops, and losing it stops the server.
  `avamux ai workloads leases` lists the ones held.

**Your own setup**, through hooks on avamux's services (`AppServices`), so machine-specific conventions live in a
plugin and not in avamux:

| Hook | What it teaches avamux |
|---|---|
| `Workloads.ScriptConventions` | How your start scripts keep their settings, when they are not plain `BIN=` / `PORT=` lines. |
| `ProcessScanner.Recognisers` | Programs to recognise as AI workloads. |
| `Workloads.ContainerPatterns` | Container names that are AI workloads. |
| `Workloads.StartHooks` | Something to do after a server starts. |
| `Workloads.EndpointResolvers` | Which local server an AI Chat provider talks to. A chat whose server is not answering then shows a bar that starts a profile for that port. |
| `Ai.SessionEnvironment` | Variables AI sessions need. |

`avamux ai workloads problems` lists sources that could not answer (another machine switched off, a gateway not set
up).

## From the command line

```sh
avamux ai workloads list                 # what runs now (--all: stopped profiles, finished tasks too; --source)
avamux ai workloads show <workload>      # everything about one, with the actions it offers
avamux ai workloads act <workload> <action>
avamux ai workloads start my-model       # stops what is in the way, waits until it answers
avamux ai workloads swap my-model --ctx 65536
avamux ai workloads cancel my-model      # while it is still loading
avamux ai workloads logs local:profile:script:my-model --lines 500
avamux ai workloads stop local:pid:12345 # anything listed, stopped its own way
avamux ai workloads models               # model files, with which profiles use them
avamux app nav workloads:models          # open a tab: running, profiles, tasks, models
```

## Settings

| Setting | Default | What it does |
|---|---|---|
| `ai.workloads` | true | Show the AI Workloads page. |
| `ai.workloadScripts` | empty | Folders of `start-*.sh` / `stop-*.sh` pairs offered as profiles (comma separated). |
| `ai.workloadModels` | empty | Folders searched for model files besides the usual places (comma separated). |

Profiles are kept in `ai-workloads.json` in the data folder.
