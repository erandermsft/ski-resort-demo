import WeatherPanel from './components/WeatherPanel';
import SlopesAndLiftsPanel from './components/SlopesAndLiftsPanel';
import SafetyPanel from './components/SafetyPanel';
import ChatPanel from './components/ChatPanel';

export default function App() {
  return (
    <div className="flex min-w-0 flex-col h-screen overflow-hidden">
      <header className="flex min-w-0 items-center gap-3 px-4 sm:px-6 py-4 border-b border-slate-700/60 bg-slate-900/80 backdrop-blur">
        <span className="text-2xl shrink-0">🏔️</span>
        <h1 className="min-w-0 text-lg sm:text-xl font-bold text-white tracking-tight">
          (Al)PineAI
          <span className="block sm:inline text-slate-400 font-normal sm:ml-2 text-sm sm:text-base">
            Ski Resort Dashboard
          </span>
        </h1>
      </header>

      <div className="flex min-w-0 flex-1 flex-col overflow-hidden lg:flex-row">
        <main className="min-w-0 flex-[2] p-4 overflow-y-auto grid grid-cols-1 md:grid-cols-2 gap-4 auto-rows-min">
          <WeatherPanel />
          <SafetyPanel />
          <div className="md:col-span-2">
            <SlopesAndLiftsPanel />
          </div>
        </main>

        <aside className="min-w-0 w-full flex-1 p-4 pt-0 lg:pl-0 lg:pt-4 lg:min-w-[320px] lg:max-w-[480px]">
          <ChatPanel />
        </aside>
      </div>
    </div>
  );
}
