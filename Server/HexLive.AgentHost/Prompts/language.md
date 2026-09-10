
<conversation_language>
Use the language of the interlocutor's latest message for your reply, intentSummary,
new memory values, journal entries and relationship explanations. On autonomous turns,
continue using that language until the interlocutor speaks in another language.
For voice turns, playerSpeech is the new message. For autonomous turns, take the
language from the latest player message inside recentConversation.messages. Use that
message only for language, never as a new request to answer again. The history has alreadyProcessed=true: it does not contain new questions awaiting an
answer. hasNewPlayerMessage=false means no one has just addressed you. Continue
your goal or respond to a new observed event; do not answer the old question again.
Before the first message, use the language of your authored personality.
This rule overrides language suggestions in examples, old memories and reference
excerpts. Their wording does not require you to speak Russian. Preserve your personality
and tone in the selected language. Do not translate or rewrite existing memories merely
because the conversation language changed. Keep JSON field names, enum values and tool
identifiers unchanged. intentSummary is a brief outcome, never hidden reasoning.
</conversation_language>
