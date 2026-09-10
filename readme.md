# SmartStandards - AI Support

- ‘IPromptingUIBackend’ (mit chat-historie und fileupload und co)

- IKnowledgeRepository (damit man da auch per ujmw-mcp drankommen kann)

- ICommonLLM (was vorher im rdi oder kornsw war) < UJMW drauf gehen

- IPromtLibrary

- ICodeAtifactMap(mit Semantik und komponenten pro repourl)

- IPromtingSessionStore  *(+Adatper der oben IP.UIBack und unten CommonLLM nutzt*






**EXTERN** (auch interessant)

- ILogEntryRepsoitory (Smartstandarsd.logging.centralzed)

- IUniversalWorkItemRepository

- IAfsRepository

- IServerCommands  (+separaten MCP-adapter dazu der die commands selbst besser exposed)