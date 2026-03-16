import { Close, Send, ChatBubbleOutline } from '@mui/icons-material';
import {
  Box,
  Fab,
  IconButton,
  Paper,
  Stack,
  TextField,
  Typography,
  CircularProgress,
  Divider,
} from '@mui/material';
import { useMemo, useRef, useState } from 'react';

type ChatMessage = {
  role: 'user' | 'assistant';
  text: string;
};

export default function ChatWidget() {
  const [open, setOpen] = useState(false);
  const [input, setInput] = useState('');
  const [sending, setSending] = useState(false);
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      role: 'assistant',
      text: 'Olá! Em que posso ajudar?',
    },
  ]);

  const listRef = useRef<HTMLDivElement | null>(null);

  const canSend = useMemo(() => input.trim().length > 0 && !sending, [input, sending]);

  const scrollToBottom = () => {
    const el = listRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  };

  const send = async () => {
    const message = input.trim();
    if (!message || sending) return;

    setSending(true);
    setInput('');
    setMessages(prev => [...prev, { role: 'user', text: message }]);

    // Let React paint the new message before scrolling.
    setTimeout(scrollToBottom, 0);

    try {
      const res = await fetch('/api/chat', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ message }),
      });

      if (!res.ok) {
        const fallback = res.status === 429
          ? 'O chat está com muitos pedidos. Tente novamente em alguns minutos.'
          : 'Não consegui responder agora. Tente novamente.';
        setMessages(prev => [...prev, { role: 'assistant', text: fallback }]);
        setTimeout(scrollToBottom, 0);
        return;
      }

      const data = (await res.json()) as { reply?: string };
      const reply = (data.reply ?? '').trim() || 'Não consegui gerar uma resposta.';
      setMessages(prev => [...prev, { role: 'assistant', text: reply }]);
      setTimeout(scrollToBottom, 0);
    } catch {
      setMessages(prev => [
        ...prev,
        { role: 'assistant', text: 'Erro de ligação. Verifique a internet e tente novamente.' },
      ]);
      setTimeout(scrollToBottom, 0);
    } finally {
      setSending(false);
    }
  };

  return (
    <Box sx={{ position: 'fixed', right: 16, bottom: 16, zIndex: theme => theme.zIndex.modal }}>
      {!open ? (
        <Fab
          color="primary"
          variant="extended"
          onClick={() => setOpen(true)}
          aria-label="Abrir chat"
        >
          <ChatBubbleOutline sx={{ mr: 1 }} />
          Chat
        </Fab>
      ) : (
        <Paper
          elevation={3}
          sx={{
            width: 360,
            maxWidth: 'calc(100vw - 32px)',
            height: 520,
            maxHeight: 'calc(100vh - 32px)',
            display: 'flex',
            flexDirection: 'column',
            overflow: 'hidden',
          }}
        >
          <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ px: 1.5, py: 1 }}>
            <Typography variant="subtitle1">Assistente</Typography>
            <IconButton onClick={() => setOpen(false)} aria-label="Fechar chat" size="small">
              <Close fontSize="small" />
            </IconButton>
          </Stack>
          <Divider />

          <Box
            ref={listRef}
            sx={{
              flex: 1,
              overflowY: 'auto',
              px: 1.5,
              py: 1.5,
              display: 'flex',
              flexDirection: 'column',
              gap: 1,
            }}
          >
            {messages.map((m, idx) => (
              <Box
                key={idx}
                sx={{
                  alignSelf: m.role === 'user' ? 'flex-end' : 'flex-start',
                  maxWidth: '90%',
                  px: 1.25,
                  py: 0.75,
                  borderRadius: 2,
                  bgcolor: m.role === 'user' ? 'action.selected' : 'background.default',
                  border: theme => `1px solid ${theme.palette.divider}`,
                }}
              >
                <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                  {m.text}
                </Typography>
              </Box>
            ))}
            {sending && (
              <Box sx={{ alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: 1 }}>
                <CircularProgress size={16} />
                <Typography variant="body2">A escrever…</Typography>
              </Box>
            )}
          </Box>

          <Divider />
          <Stack direction="row" spacing={1} sx={{ p: 1.25 }}>
            <TextField
              fullWidth
              size="small"
              placeholder="Escreva a sua mensagem…"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault();
                  void send();
                }
              }}
              disabled={sending}
            />
            <IconButton
              color="primary"
              onClick={() => void send()}
              disabled={!canSend}
              aria-label="Enviar"
            >
              <Send />
            </IconButton>
          </Stack>
        </Paper>
      )}
    </Box>
  );
}
