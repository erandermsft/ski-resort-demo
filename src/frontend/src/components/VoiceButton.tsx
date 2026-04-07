import { useCallback, useRef, useState } from 'react';
import { VoiceSession, type VoiceStatus, type VoiceTranscript } from '../lib/VoiceSession';

interface VoiceButtonProps {
    onTranscript: (transcript: VoiceTranscript) => void;
    disabled?: boolean;
    conversationId?: string;
}

const STATUS_LABELS: Record<VoiceStatus, string> = {
    disconnected: '',
    connecting: 'Connecting...',
    ready: 'Voice active',
    listening: '🎤 Listening...',
    processing: '🤔 Processing...',
    function_calling: '🔧 Searching...',
};

export default function VoiceButton({ onTranscript, disabled, conversationId }: VoiceButtonProps) {
    const [status, setStatus] = useState<VoiceStatus>('disconnected');
    const [error, setError] = useState<string | null>(null);
    const sessionRef = useRef<VoiceSession | null>(null);

    const isActive = status !== 'disconnected';

    const toggleVoice = useCallback(async () => {
        if (isActive) {
            await sessionRef.current?.stop();
            sessionRef.current = null;
            return;
        }

        setError(null);
        const session = new VoiceSession({
            onTranscript,
            onStatus: (newStatus) => setStatus(newStatus),
            onError: (msg) => {
                setError(msg);
                console.error('Voice error:', msg);
            },
        }, conversationId);
        sessionRef.current = session;
        await session.start();
    }, [isActive, onTranscript, conversationId]);

    const statusLabel = STATUS_LABELS[status];

    return (
        <div className="flex items-center gap-2">
            <button
                className={`rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
                    isActive
                        ? 'bg-red-600 text-white hover:bg-red-500 animate-pulse'
                        : 'bg-slate-700 text-slate-300 hover:bg-slate-600 hover:text-white'
                } disabled:opacity-50`}
                onClick={toggleVoice}
                disabled={disabled}
                title={isActive ? 'Stop voice session' : 'Start voice session'}
            >
                {isActive ? '🔊 Stop' : '🎙️ Voice'}
            </button>
            {statusLabel && (
                <span className="text-xs text-slate-400">{statusLabel}</span>
            )}
            {error && (
                <span className="text-xs text-red-400" title={error}>⚠️</span>
            )}
        </div>
    );
}
