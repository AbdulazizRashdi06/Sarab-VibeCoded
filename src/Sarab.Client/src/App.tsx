import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { createClient, type SupabaseClient } from '@supabase/supabase-js'
import {
  BookOpen,
  ChevronLeft,
  ChevronRight,
  CirclePlus,
  Eye,
  Gamepad2,
  ImagePlus,
  Languages,
  Palette,
  PartyPopper,
  Play,
  Save,
  Send,
  Settings,
  Shield,
  Sparkles,
  Trash2,
  Upload,
  Volume2,
  VolumeX,
  Vote,
} from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type RoomPhase = 'Lobby' | 'Answer' | 'SelfReport' | 'Vote' | 'Results' | 'GameOver'
type ConfidenceLevel = 'Low' | 'Medium' | 'High'
type Locale = 'en' | 'ar'
type AvatarGender = 'Male' | 'Female'
type AvatarPartType = 'Clothes' | 'Face' | 'Headwear'

type AvatarTransform = {
  x: number
  y: number
  scale: number
  rotation: number
}

type PlayerAvatar = {
  gender: AvatarGender
  skinColor: string
  clothesId?: string | null
  faceId?: string | null
  headwearId?: string | null
}

type AvatarPart = {
  id: string
  type: AvatarPartType
  name: string
  imageUrl: string
  supportsMale: boolean
  supportsFemale: boolean
  active: boolean
  maleTransform?: AvatarTransform | null
  femaleTransform?: AvatarTransform | null
}

type CatalogCategory = {
  categoryId: string
  packId: string
  packName: string
  categoryName: string
  language: string
  roundCount: number
}

type Player = {
  id: string
  name: string
  score: number
  avatar: PlayerAvatar
  connected: boolean
  isHost: boolean
  isBot: boolean
  ready: boolean
  waitingForNextRound: boolean
  submittedAnswer: boolean
  selfReported: boolean
  safeBet: boolean
  selfReportDone: boolean
  voted: boolean
}

type Answer = {
  id: string
  text: string
  authorId?: string
  authorName?: string
  isMine: boolean
  isImposterAnswer: boolean
}

type ScoreEvent = {
  playerId: string
  reason: string
  delta: number
  detail: string
}

type RoundResult = {
  roundNumber: number
  pool: number
  rollover: number
  imposterPlayerId: string
  imposterAnswerId: string
  majorityPrompt: string
  alternatePrompt: string
  events: ScoreEvent[]
  highlights: string[]
}

type RoomSnapshot = {
  code: string
  phase: RoomPhase
  you?: string
  yourPrompt?: string
  hostName: string
  currentRound: number
  totalRounds: number
  rollover: number
  phaseEndsAt?: string
  players: Player[]
  answers: Answer[]
  lastResult?: RoundResult
  categories: CatalogCategory[]
  warning?: string
}

type AdminPack = {
  id: string
  name: string
  language: string
  active: boolean
  categoryCount: number
  roundCount: number
}

type PackPreview = {
  name: string
  language: string
  categories: number
  rounds: number
  valid: boolean
  errors: string[]
}

const apiBase = import.meta.env.VITE_API_BASE ?? ''
const supabaseUrl = import.meta.env.VITE_SUPABASE_URL
const supabaseAnonKey = import.meta.env.VITE_SUPABASE_ANON_KEY
const brandLogos = {
  en: new URL('./assets/logo/sarab-logo-en.png', import.meta.url).href,
  ar: new URL('./assets/logo/sarab-logo-ar.png', import.meta.url).href,
} satisfies Record<Locale, string>
const skinSwatches = ['#7A4A32', '#9B6041', '#B97855', '#D0926D', '#E4B08A', '#F0C9A2']
const defaultAvatar: PlayerAvatar = {
  gender: 'Male',
  skinColor: '#B97855',
  clothesId: null,
  faceId: null,
  headwearId: null,
}

const copy = {
  en: {
    subtitle:
      "The one-word desert bluff. Everyone writes for a secret prompt, but somewhere at the table a mirage shifted one player's word.",
    create: 'Create room',
    join: 'Join room',
    name: 'Your name',
    code: 'Room code',
    admin: 'Admin',
    game: 'Game',
    prompt: 'Your prompt',
    answer: 'One word answer',
    selfReport: 'I saw the mirage',
    safeBet: "Bet I'm safe",
    neutral: 'Stay neutral',
    vote: 'Vote',
    confidence: 'Confidence',
    start: 'Start game',
    next: 'Next phase',
    end: 'End game',
    upload: 'Upload pack',
  },
  ar: {
    subtitle:
      'لعبة كلمة وحدة وسط السراب. كل لاعب يحصل كلمة سرية، وواحد منكم يمكن كلمته تبدلت بدون ما يعرف.',
    create: 'افتح غرفة',
    join: 'ادخل غرفة',
    name: 'اسمك',
    code: 'رمز الغرفة',
    admin: 'الإدارة',
    game: 'اللعبة',
    prompt: 'الكلمة السرية',
    answer: 'جواب من كلمة',
    selfReport: 'شفت السراب',
    safeBet: 'أراهن إني آمن',
    neutral: 'محايد',
    vote: 'صوّت',
    confidence: 'الثقة',
    start: 'ابدأ اللعبة',
    next: 'المرحلة التالية',
    end: 'أنه اللعبة',
    upload: 'ارفع الحزمة',
  },
}

function App() {
  const [locale, setLocale] = useState<Locale>('en')
  const t = copy[locale]
  const [mode, setMode] = useState<'game' | 'admin'>('game')
  const [connection, setConnection] = useState<HubConnection | null>(null)
  const [room, setRoom] = useState<RoomSnapshot | null>(null)
  const [avatarParts, setAvatarParts] = useState<AvatarPart[]>([])
  const [avatar, setAvatar] = useState<PlayerAvatar>(() => loadSavedAvatar())
  const [soundMuted, setSoundMuted] = useState(() => localStorage.getItem('sarab:muted') === 'true')
  const [name, setName] = useState(localStorage.getItem('sarab:name') ?? '')
  const [roomCode, setRoomCode] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    document.documentElement.lang = locale === 'ar' ? 'ar-OM' : 'en'
    document.documentElement.dir = locale === 'ar' ? 'rtl' : 'ltr'
  }, [locale])

  useEffect(() => {
    return () => {
      void connection?.stop()
    }
  }, [connection])

  useEffect(() => {
    fetch(`${apiBase}/api/avatar/parts`)
      .then((response) => response.ok ? response.json() : [])
      .then((parts: AvatarPart[]) => {
        setAvatarParts(parts)
        setAvatar((current) => normalizeAvatarSelection(current, parts))
      })
      .catch(() => setAvatarParts([]))
  }, [])

  async function ensureConnection() {
    if (connection?.state === 'Connected') {
      return connection
    }

    const next = new HubConnectionBuilder()
      .withUrl(`${apiBase}/hubs/game`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build()

    next.on('roomUpdated', (snapshot: RoomSnapshot) => {
      setRoom(snapshot)
      setError('')
    })

    await next.start()
    setConnection(next)
    return next
  }

  async function callHub<T>(action: (hub: HubConnection) => Promise<T>) {
    try {
      setError('')
      const hub = await ensureConnection()
      return await action(hub)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.')
      return null
    }
  }

  async function createRoom(event: FormEvent) {
    event.preventDefault()
    const playerName = name.trim()
    if (!playerName) {
      setError('Add your name first.')
      return
    }
    localStorage.setItem('sarab:name', playerName)
    const snapshot = await callHub((hub) =>
      hub.invoke<RoomSnapshot>('CreateRoom', { playerName, locale, avatar }),
    )
    if (snapshot) {
      playSound('success', soundMuted)
      setRoom(snapshot)
    }
  }

  async function joinRoom(event: FormEvent) {
    event.preventDefault()
    const playerName = name.trim()
    const code = roomCode.trim().toUpperCase()
    if (!playerName) {
      setError('Add your name first.')
      return
    }
    if (!code) {
      setError('Enter a room code.')
      return
    }
    localStorage.setItem('sarab:name', playerName)
    const snapshot = await callHub((hub) =>
      hub.invoke<RoomSnapshot>('JoinRoom', { roomCode: code, playerName, locale, avatar }),
    )
    if (snapshot) {
      playSound('success', soundMuted)
      setRoom(snapshot)
    }
  }

  async function updateAvatar(nextAvatar: PlayerAvatar) {
    const normalized = normalizeAvatarSelection(nextAvatar, avatarParts)
    setAvatar(normalized)
    localStorage.setItem('sarab:avatar', JSON.stringify(normalized))
    if (room) {
      await callHub((hub) => hub.invoke('UpdateAvatar', normalized))
    }
  }

  async function updateReady(ready: boolean) {
    playSound(ready ? 'ready' : 'click', soundMuted)
    await callHub((hub) => hub.invoke('UpdateReady', ready))
  }

  function toggleSound() {
    const next = !soundMuted
    setSoundMuted(next)
    localStorage.setItem('sarab:muted', String(next))
    if (!next) playSound('click', false)
  }

  const you = room?.players.find((player) => player.id === room.you)
  const isHost = you?.isHost ?? false

  return (
    <main className={room ? 'app-shell in-room' : 'app-shell'}>
      <div className="utility-actions" aria-label="App controls">
        <button className="icon-button" type="button" onClick={() => setLocale(locale === 'en' ? 'ar' : 'en')} title="Language">
          <Languages size={20} />
        </button>
        <button className="icon-button" type="button" onClick={toggleSound} title={soundMuted ? 'Sound off' : 'Sound on'}>
          {soundMuted ? <VolumeX size={20} /> : <Volume2 size={20} />}
        </button>
        <button className="mode-button" type="button" onClick={() => setMode(mode === 'game' ? 'admin' : 'game')}>
          {mode === 'game' ? <Shield size={18} /> : <Sparkles size={18} />}
          {mode === 'game' ? t.admin : t.game}
        </button>
      </div>

      {mode === 'admin' ? (
        <AdminPanel />
      ) : room ? (
        <GameRoom
          room={room}
          isHost={isHost}
          locale={locale}
          avatarParts={avatarParts}
          t={t}
          soundMuted={soundMuted}
          onAvatarChange={updateAvatar}
          onReadyChange={updateReady}
          onAction={(method, payload) =>
            callHub((hub) => (payload === undefined ? hub.invoke(method) : hub.invoke(method, payload)))
          }
        />
      ) : (
        <HomeScreen
          t={t}
          locale={locale}
          avatar={avatar}
          avatarParts={avatarParts}
          onAvatarChange={updateAvatar}
          name={name}
          roomCode={roomCode}
          setName={setName}
          setRoomCode={setRoomCode}
          onCreate={createRoom}
          onJoin={joinRoom}
        />
      )}

      {!room && mode === 'game' && <nav className="bottom-nav" aria-label="Mobile navigation">
        <a className="active" href="#play">
          <Gamepad2 size={22} />
          <span>Play</span>
        </a>
        <a href="#rules">
          <BookOpen size={22} />
          <span>Rules</span>
        </a>
        <a
          href="#admin"
          onClick={(event) => {
            event.preventDefault()
            setMode('admin')
          }}
        >
          <Shield size={22} />
          <span>Admin</span>
        </a>
      </nav>}

      {!room && <aside className="desktop-rail" aria-label="Quick links">
        <a href="#rules" title="Rules">
          <BookOpen size={22} />
        </a>
        <button type="button" onClick={() => setMode(mode === 'game' ? 'admin' : 'game')} title={mode === 'game' ? t.admin : t.game}>
          {mode === 'game' ? <Shield size={22} /> : <Sparkles size={22} />}
        </button>
      </aside>}

      {error && <p className="toast">{error}</p>}
    </main>
  )
}

function BrandLogo({ locale, variant }: { locale: Locale; variant: 'hero' | 'screen' }) {
  return (
    <img
      className={`brand-logo brand-logo-${variant}`}
      src={brandLogos[locale]}
      alt="Sarab"
      draggable={false}
    />
  )
}

function HomeScreen({
  t,
  locale,
  avatar,
  avatarParts,
  onAvatarChange,
  name,
  roomCode,
  setName,
  setRoomCode,
  onCreate,
  onJoin,
}: {
  t: typeof copy.en
  locale: Locale
  avatar: PlayerAvatar
  avatarParts: AvatarPart[]
  onAvatarChange: (avatar: PlayerAvatar) => void
  name: string
  roomCode: string
  setName: (value: string) => void
  setRoomCode: (value: string) => void
  onCreate: (event: FormEvent) => void
  onJoin: (event: FormEvent) => void
}) {
  const canCreate = name.trim().length > 0
  const canJoin = canCreate && roomCode.trim().length > 0

  return (
    <section className="home-stage" id="play">
      <div className="hero-copy">
        <p className="eyebrow">The one-word party bluff</p>
        <BrandLogo locale={locale} variant="hero" />
        <p>{t.subtitle}</p>
      </div>

      <div className="action-stack">
        <AvatarPicker
          avatar={avatar}
          parts={avatarParts}
          onChange={onAvatarChange}
        />

        <label className="name-slip">
          <span>{t.name}</span>
          <input value={name} onChange={(event) => setName(event.target.value)} autoComplete="name" placeholder="Aziz" />
        </label>

        <form className="join-card" onSubmit={onJoin}>
          <div className="join-row">
            <input
              aria-label={t.code}
              value={roomCode}
              onChange={(event) => setRoomCode(event.target.value.toUpperCase())}
              placeholder="Enter Room Code"
              maxLength={6}
            />
            <button className="primary square" type="submit" aria-label={t.join} disabled={!canJoin}>
              <Play size={24} />
            </button>
          </div>
        </form>

        <div className="or-row">
          <i />
          <span>or</span>
          <i />
        </div>

        <form onSubmit={onCreate}>
          <button className="secondary create-button" type="submit" disabled={!canCreate}>
            <CirclePlus size={32} />
            {t.create}
          </button>
        </form>
      </div>

      <section className="how-to" id="rules">
        <h2>How to Play?</h2>
        <div className="how-grid">
          <div>
            <CirclePlus size={40} />
            <p>Gather<br />Friends</p>
          </div>
          <div>
            <Gamepad2 size={40} />
            <p>Join<br />Room</p>
          </div>
          <div>
            <PartyPopper size={40} />
            <p>Have<br />Fun!</p>
          </div>
        </div>
      </section>
    </section>
  )
}

function AvatarPicker({
  avatar,
  parts,
  compact,
  onChange,
}: {
  avatar: PlayerAvatar
  parts: AvatarPart[]
  compact?: boolean
  onChange: (avatar: PlayerAvatar) => void
}) {
  const rows: Array<{ type: AvatarPartType; label: string }> = [
    { type: 'Clothes', label: 'Clothes' },
    { type: 'Face', label: 'Face' },
    { type: 'Headwear', label: 'Hair' },
  ]

  function setGender(gender: AvatarGender) {
    onChange(normalizeAvatarSelection({ ...avatar, gender }, parts))
  }

  return (
    <section className={compact ? 'avatar-picker compact' : 'avatar-picker'} aria-label="Avatar picker">
      <div className="avatar-gender" aria-label="Gender">
        {(['Male', 'Female'] as AvatarGender[]).map((gender) => (
          <button
            className={avatar.gender === gender ? 'chip active' : 'chip'}
            key={gender}
            type="button"
            onClick={() => setGender(gender)}
          >
            {gender}
          </button>
        ))}
      </div>

      <div className="avatar-builder">
        <div className="avatar-arrows previous">
          {rows.map((row) => (
            <button
              className="icon-button"
              key={row.type}
              type="button"
              title={`Previous ${row.label}`}
              onClick={() => onChange(cycleAvatarPart(avatar, parts, row.type, -1))}
            >
              <ChevronLeft size={20} />
            </button>
          ))}
        </div>
        <AvatarView avatar={avatar} parts={parts} size={compact ? 'medium' : 'large'} />
        <div className="avatar-arrows next">
          {rows.map((row) => (
            <button
              className="icon-button"
              key={row.type}
              type="button"
              title={`Next ${row.label}`}
              onClick={() => onChange(cycleAvatarPart(avatar, parts, row.type, 1))}
            >
              <ChevronRight size={20} />
            </button>
          ))}
        </div>
      </div>

      <div className="avatar-labels" aria-hidden="true">
        {rows.map((row) => <span key={row.type}>{row.label}</span>)}
      </div>

      <div className="skin-row" aria-label="Skin color">
        {skinSwatches.map((color) => (
          <button
            className={avatar.skinColor.toUpperCase() === color ? 'skin-swatch active' : 'skin-swatch'}
            key={color}
            style={{ backgroundColor: color }}
            type="button"
            title={color}
            onClick={() => onChange({ ...avatar, skinColor: color })}
          />
        ))}
        <label className="custom-skin" title="Custom skin color">
          <Palette size={16} />
          <input
            value={isValidHexColor(avatar.skinColor) ? avatar.skinColor : defaultAvatar.skinColor}
            type="color"
            onChange={(event) => onChange({ ...avatar, skinColor: event.target.value.toUpperCase() })}
          />
        </label>
      </div>
    </section>
  )
}

function AvatarView({ avatar, parts, size = 'medium' }: { avatar: PlayerAvatar; parts: AvatarPart[]; size?: 'mini' | 'small' | 'medium' | 'large' }) {
  const selected = selectedAvatarParts(avatar, parts)
  const headShape = avatar.gender === 'Female'
    ? 'M512 178 C606 178 678 252 678 350 C678 450 606 520 512 520 C418 520 346 450 346 350 C346 252 418 178 512 178 Z'
    : 'M512 176 C604 176 670 244 670 342 C670 444 604 516 512 516 C420 516 354 444 354 342 C354 244 420 176 512 176 Z'
  const bodyShape = avatar.gender === 'Female'
    ? 'M328 940 C352 710 414 574 512 574 C610 574 672 710 696 940 C610 984 414 984 328 940 Z'
    : 'M296 940 C326 700 398 574 512 574 C626 574 698 700 728 940 C628 986 396 986 296 940 Z'

  return (
    <div className={`avatar-view avatar-${size}`} aria-hidden="true">
      <svg viewBox="0 0 1024 1024" role="img">
        <path className="avatar-shadow" d="M280 952 Q512 1015 744 952 Q690 1008 512 1016 Q334 1008 280 952 Z" />
        <path d={bodyShape} fill={avatar.skinColor} />
        <path d="M452 510 H572 V626 H452 Z" fill={avatar.skinColor} />
        <path d={headShape} fill={avatar.skinColor} />
        <path className="avatar-line" d="M376 615 Q512 692 648 615" />
        {selected.map(({ part, transform }) => (
          <image
            href={part.imageUrl}
            key={part.id}
            width="1024"
            height="1024"
            preserveAspectRatio="xMidYMid meet"
            transform={avatarTransform(transform)}
          />
        ))}
      </svg>
    </div>
  )
}

function PhaseBanner({ phase }: { phase: RoomPhase }) {
  const steps: RoomPhase[] = ['Lobby', 'Answer', 'SelfReport', 'Vote', 'Results']
  const index = Math.max(0, steps.indexOf(phase === 'GameOver' ? 'Results' : phase))
  const title = {
    Lobby: 'Set up the room',
    Answer: 'Write one word',
    SelfReport: 'Read the words',
    Vote: 'Find the mirage',
    Results: 'Score reveal',
    GameOver: 'Final scores',
  }[phase]
  const detail = {
    Lobby: 'Pick a category, share the code, then start when everyone is ready.',
    Answer: 'Look at your secret prompt and submit exactly one word.',
    SelfReport: 'Answers are anonymous. Claim the mirage only if your word felt wrong.',
    Vote: 'Choose one answer that feels like it came from the shifted prompt.',
    Results: 'See who caught the mirage, who lost points, and what rolls over.',
    GameOver: 'The clearest player wins.',
  }[phase]

  return (
    <section className="phase-banner" aria-label="Current phase">
      <div>
        <span>{phaseEyebrow(phase)}</span>
        <h2>{title}</h2>
        <p>{detail}</p>
      </div>
      <ol className="phase-track" aria-label="Game progress">
        {steps.map((step, stepIndex) => (
          <li className={stepIndex <= index ? 'active' : ''} key={step}>
            {stepIndex + 1}
          </li>
        ))}
      </ol>
    </section>
  )
}

function RoundGuide() {
  const steps = [
    ['1', 'Secret prompt', 'Everyone gets a word. One player quietly gets the mirage prompt.'],
    ['2', 'One answer', 'Write exactly one word. Answers stay anonymous while everyone judges.'],
    ['3', 'Claim or vote', 'Claim if your word felt wrong, then vote for the answer that looks shifted.'],
    ['4', 'Score reveal', 'Confidence, speed, claims, and penalties decide where the pool goes.'],
  ]

  return (
    <section className="round-guide" aria-label="Round guide">
      {steps.map(([number, title, detail]) => (
        <div key={number}>
          <b>{number}</b>
          <strong>{title}</strong>
          <span>{detail}</span>
        </div>
      ))}
    </section>
  )
}

function GameRoom({
  room,
  isHost,
  locale,
  avatarParts,
  t,
  soundMuted,
  onAvatarChange,
  onReadyChange,
  onAction,
}: {
  room: RoomSnapshot
  isHost: boolean
  locale: Locale
  avatarParts: AvatarPart[]
  t: typeof copy.en
  soundMuted: boolean
  onAvatarChange: (avatar: PlayerAvatar) => void
  onReadyChange: (ready: boolean) => Promise<void>
  onAction: (method: string, payload?: unknown) => Promise<unknown>
}) {
  const [categoryId, setCategoryId] = useState(room.categories[0]?.categoryId ?? '')
  const [rounds, setRounds] = useState(8)
  const [answerSeconds, setAnswerSeconds] = useState(30)
  const [selfReportSeconds, setSelfReportSeconds] = useState(15)
  const [voteSeconds, setVoteSeconds] = useState(30)
  const [answer, setAnswer] = useState('')
  const [selectedAnswer, setSelectedAnswer] = useState('')
  const [confidence, setConfidence] = useState<ConfidenceLevel>('Medium')
  const [promptSeenRound, setPromptSeenRound] = useState(0)
  const [poolSeenRound, setPoolSeenRound] = useState(0)
  const timerWarningRef = useRef('')
  const autoExpireRef = useRef('')
  const timer = useCountdown(room.phaseEndsAt)

  useEffect(() => {
    if (room.categories.length > 0 && !room.categories.some((category) => category.categoryId === categoryId)) {
      setCategoryId(room.categories[0].categoryId)
    }
  }, [categoryId, room.categories])

  useEffect(() => {
    setPromptSeenRound(0)
    setPoolSeenRound(0)
    setAnswer('')
    setSelectedAnswer('')
  }, [room.currentRound])

  useEffect(() => {
    if (timer === '5s' && room.phaseEndsAt && timerWarningRef.current !== `${room.phaseEndsAt}:${room.phase}`) {
      timerWarningRef.current = `${room.phaseEndsAt}:${room.phase}`
      playSound('timer', soundMuted)
    }
  }, [room.phase, room.phaseEndsAt, soundMuted, timer])

  useEffect(() => {
    if (!room.phaseEndsAt || timer !== '0s' || room.phase === 'Lobby' || room.phase === 'Results' || room.phase === 'GameOver') {
      return
    }

    const expireKey = `${room.phaseEndsAt}:${room.phase}`
    if (autoExpireRef.current === expireKey) {
      return
    }

    const timeout = window.setTimeout(() => {
      autoExpireRef.current = expireKey
      void onAction('ExpirePhase')
    }, 1200)

    return () => window.clearTimeout(timeout)
  }, [onAction, room.phase, room.phaseEndsAt, timer])

  const scoreboard = [...room.players].sort((a, b) => b.score - a.score)
  const you = room.players.find((player) => player.id === room.you)
  const readyPlayers = room.players.filter((player) => player.ready).length
  const activeLobbyPlayers = room.players.filter((player) => !player.waitingForNextRound).length
  const allReady = activeLobbyPlayers > 0 && readyPlayers >= activeLobbyPlayers
  const promptSeen = promptSeenRound === room.currentRound || Boolean(you?.submittedAnswer)
  const poolSeen = poolSeenRound === room.currentRound || room.phase !== 'SelfReport'
  const surfaceClass = [
    'play-surface',
    `phase-${room.phase.toLowerCase()}`,
    room.phase === 'Answer' && !promptSeen ? 'phase-prompt-reveal' : '',
    room.phase === 'Answer' && promptSeen ? 'phase-write' : '',
    room.phase === 'SelfReport' && !poolSeen ? 'phase-pool' : '',
    room.phase === 'SelfReport' && poolSeen ? 'phase-tell' : '',
    room.phase === 'Results' && room.lastResult?.rollover ? 'phase-jackpot' : '',
    room.phase === 'Results' && !room.lastResult?.rollover ? 'phase-payout' : '',
  ].filter(Boolean).join(' ')
  return (
    <section className="room-layout">
      <aside className="scoreboard">
            <div className="room-code">
          <span>Oasis room</span>
          <strong>{room.code}</strong>
        </div>
        {room.warning && <p className="warning">{room.warning}</p>}
        <div className="players">
          {scoreboard.map((player) => (
            <div className="player-row" key={player.id}>
              <AvatarView avatar={player.avatar} parts={avatarParts} size="mini" />
              <div>
                <strong>{player.name}</strong>
                <small>
                  {player.isBot ? `Bot - ${statusFor(player, room.phase)}` : playerStatusText(player, room.phase)}
                </small>
              </div>
              <div className="player-score-stack">
                <span className={player.connected ? 'dot online' : 'dot'} />
                <b>{player.score}</b>
              </div>
            </div>
          ))}
        </div>
      </aside>

      <section className={surfaceClass}>
        <div className="round-strip">
          <span>Round {room.currentRound || 1} of {room.totalRounds || rounds}</span>
          {timer !== '--' && <b>{timer}</b>}
        </div>
        <PhaseBanner phase={room.phase} />

        {room.phase === 'Lobby' && (
          <div className="phase-panel">
            <h2>Gather the table</h2>
            <p>Share the room code, pick a category, and let everyone mark ready before the first word hits the sand.</p>
            <div className="lobby-status">
              <div>
                <span>Players</span>
                <b>{activeLobbyPlayers}/20</b>
              </div>
              <div>
                <span>Ready</span>
                <b>{readyPlayers}/{activeLobbyPlayers}</b>
              </div>
              <div>
                <span>Start check</span>
                <b>{activeLobbyPlayers < 4 ? 'Need 4' : allReady ? 'All set' : 'Waiting'}</b>
              </div>
            </div>
            <RoundGuide />
            {you && (
              <button
                className={you.ready ? 'secondary ready-toggle active' : 'ghost ready-toggle'}
                type="button"
                onClick={() => void onReadyChange(!you.ready)}
              >
                {you.ready ? 'Ready' : 'Mark ready'}
              </button>
            )}
            {you && (
              <AvatarPicker
                avatar={you.avatar}
                parts={avatarParts}
                compact
                onChange={onAvatarChange}
              />
            )}
            {isHost && (
              <div className="settings-grid">
                <label>
                  Category
                  <select value={categoryId} onChange={(event) => setCategoryId(event.target.value)}>
                    {room.categories.map((category) => (
                      <option key={category.categoryId} value={category.categoryId}>
                        {category.packName} / {category.categoryName} ({category.language})
                      </option>
                    ))}
                  </select>
                </label>
                <NumberInput label="Rounds" value={rounds} min={1} max={50} onChange={setRounds} />
                <NumberInput label="Answer seconds" value={answerSeconds} min={10} max={180} onChange={setAnswerSeconds} />
                <NumberInput label="Claim seconds" value={selfReportSeconds} min={5} max={90} onChange={setSelfReportSeconds} />
                <NumberInput label="Vote seconds" value={voteSeconds} min={10} max={180} onChange={setVoteSeconds} />
                <button
                  className="secondary wide"
                  type="button"
                  onClick={() => {
                    playSound('ready', soundMuted)
                    return onAction('AddDevBots', 3)
                  }}
                >
                  <CirclePlus size={18} />
                  Add 3 dev bots
                </button>
                <button
                  className="primary wide"
                  type="button"
                  disabled={!categoryId || activeLobbyPlayers < 4}
                  onClick={() => {
                    playSound('start', soundMuted)
                    return onAction('StartGame', {
                      categoryId,
                      totalRounds: rounds,
                      answerSeconds,
                      selfReportSeconds,
                      voteSeconds,
                    })
                  }}
                >
                  <Play size={18} />
                  {allReady ? t.start : 'Start anyway'}
                </button>
              </div>
            )}
          </div>
        )}

        {room.phase === 'Answer' && !promptSeen && (
          <div className="phase-panel prompt-reveal-panel">
            <BrandLogo locale={locale} variant="screen" />
            <span className="screen-pill">ROUND {room.currentRound} OF {room.totalRounds}</span>
            <div className="secret-card">
              <span>{room.yourPrompt}</span>
            </div>
            <p>Write one word that belongs with it. Keep it a little vague; obvious words evaporate in the heat.</p>
            <button className="primary wide" type="button" onClick={() => {
              playSound('reveal', soundMuted)
              setPromptSeenRound(room.currentRound)
            }}>
              I've got it
            </button>
            <small>{room.players.length} players - one of you is chasing a mirage</small>
          </div>
        )}

        {room.phase === 'Answer' && promptSeen && !you?.submittedAnswer && (
          <form
            className="phase-panel prompt-panel"
            onSubmit={(event) => {
              event.preventDefault()
              playSound('submit', soundMuted)
              void onAction('SubmitAnswer', { answer })
            }}
          >
            <span>{t.prompt}</span>
            <strong>{room.yourPrompt}</strong>
            <p className="hint-strip">Write one word that belongs with it. Keep it a little vague; obvious words evaporate in the heat.</p>
            <TextInput label={t.answer} value={answer} onChange={setAnswer} />
            <button className="primary" type="submit" disabled={!answer.trim()}>
              <Send size={18} />
              Send it into the sand
            </button>
          </form>
        )}

        {room.phase === 'Answer' && promptSeen && you?.submittedAnswer && (
          <div className="phase-panel waiting-panel">
            <Send size={34} />
            <h2>Word locked</h2>
            <p>Your answer is in. Waiting for the rest of the table.</p>
            <div className="mini-roster">
              {room.players.map((player) => (
                <span className={player.submittedAnswer ? 'done' : ''} key={player.id}>
                  {player.name}
                </span>
              ))}
            </div>
          </div>
        )}

        {room.phase === 'SelfReport' && !poolSeen && (
          <div className="phase-panel pool-panel">
            <span className="screen-tag gold">ROUND {room.currentRound} - THE POOL</span>
            <h2>{room.answers.length || room.players.length} words rise<br />from the heat</h2>
            <p>Whose is whose? No names yet.</p>
            <AnswerBoard room={room} />
            <div className="mechanic-note">
              <span>*</span>
              <p>Matching and obvious words shimmer before judging begins.</p>
            </div>
            <button className="primary wide" type="button" onClick={() => {
              playSound('reveal', soundMuted)
              setPoolSeenRound(room.currentRound)
            }}>
              Read them, then judge
            </button>
          </div>
        )}

        {room.phase === 'SelfReport' && poolSeen && (
          <div className="phase-panel tell-panel">
            <span className="screen-tag gold">ROUND {room.currentRound} - THE TELL</span>
            <h2>Did you see the mirage?</h2>
            <p>If your word felt off, you may have chased water that was not there.</p>
            {you?.selfReportDone ? (
              <div className="claim-locked">
                <Eye size={18} />
                <strong>{you.selfReported ? 'Claim locked' : you.safeBet ? 'Safe bet locked' : 'Neutral locked'}</strong>
                <span>Waiting for the table. Voting opens automatically when everyone is done.</span>
              </div>
            ) : (
              <div className="claim-actions">
                <button className="danger" type="button" onClick={() => {
                  playSound('claim', soundMuted)
                  return onAction('SelfReport')
                }}>
                  <Eye size={18} />
                  {t.selfReport}
                </button>
                <button className="secondary" type="button" onClick={() => {
                  playSound('ready', soundMuted)
                  return onAction('BetSafe')
                }}>
                  {t.safeBet}
                </button>
                <button className="ghost neutral-choice" type="button" onClick={() => {
                  playSound('click', soundMuted)
                  return onAction('FinishSelfReport')
                }}>
                  {t.neutral}
                </button>
              </div>
            )}
            <ClaimList players={room.players} />
            <div className="mini-roster">
              {room.players.map((player) => (
                <span className={player.selfReportDone ? 'done' : ''} key={player.id}>
                  {player.name}
                </span>
              ))}
            </div>
            <p className="tell-footnote">Claim if you think you are the mirage. Bet safe for a small reward or penalty. Neutral has no risk.</p>
          </div>
        )}

        {room.phase === 'Vote' && !you?.voted && (
          <form
            className="phase-panel"
            onSubmit={(event) => {
              event.preventDefault()
              playSound('vote', soundMuted)
              void onAction('SubmitVote', { answerId: selectedAnswer, confidence })
            }}
          >
            <span className="screen-tag teal">ROUND {room.currentRound} - VOTE</span>
            <div className="vote-intel">
              <Eye size={16} />
              <span>{claimText(room.players)}</span>
            </div>
            <AnswerBoard room={room} selected={selectedAnswer} onSelect={setSelectedAnswer} />
            <div className="confidence-row" aria-label={t.confidence}>
              {(['Low', 'Medium', 'High'] as ConfidenceLevel[]).map((level) => (
                <button
                  key={level}
                  className={confidence === level ? 'chip active' : 'chip'}
                  type="button"
                  onClick={() => setConfidence(level)}
                >
                  {level}
                </button>
              ))}
            </div>
            <p className="confidence-help">Low is safe. High can win more, but hurts more if you are wrong.</p>
            <button className="primary" type="submit" disabled={!selectedAnswer}>
              <Vote size={18} />
              {t.vote}
            </button>
          </form>
        )}

        {room.phase === 'Vote' && you?.voted && (
          <div className="phase-panel waiting-panel">
            <Vote size={34} />
            <h2>Vote locked</h2>
            <p>Your vote is in. Scores reveal when everyone is done.</p>
            <div className="mini-roster">
              {room.players.map((player) => (
                <span className={player.voted ? 'done' : ''} key={player.id}>
                  {player.name}
                </span>
              ))}
            </div>
          </div>
        )}

        {(room.phase === 'Results' || room.phase === 'GameOver') && (
          <div className={room.phase === 'GameOver' ? 'phase-panel final-panel' : 'phase-panel'}>
            {room.phase === 'Results' && room.lastResult && <RoundLeaderboard result={room.lastResult} players={room.players} parts={avatarParts} />}
            {room.phase === 'GameOver' && <FinalLeaderboard players={room.players} parts={avatarParts} />}
          </div>
        )}

        {isHost && room.phase !== 'Lobby' && room.phase !== 'GameOver' && (room.phase !== 'Answer' || Boolean(you?.submittedAnswer)) && (room.phase !== 'SelfReport' || poolSeen) && (
          room.phase === 'SelfReport' ? (
            <button className="text-advance" type="button" onClick={() => {
              playSound('click', soundMuted)
              return onAction('AdvancePhase')
            }}>
              Open voting
            </button>
          ) : (
            <div className="host-bar">
              <button className="secondary" type="button" onClick={() => {
                playSound(room.phase === 'Vote' ? 'reveal' : 'click', soundMuted)
                return onAction('AdvancePhase')
              }}>
                <Settings size={18} />
                {hostAdvanceLabel(room.phase)}
              </button>
              <button className="ghost" type="button" onClick={() => {
                playSound('reveal', soundMuted)
                return onAction('EndGame')
              }}>
                {t.end}
              </button>
            </div>
          )
        )}
      </section>
    </section>
  )
}

function AdminPanel() {
  const [client] = useState<SupabaseClient | null>(() =>
    supabaseUrl && supabaseAnonKey ? createClient(supabaseUrl, supabaseAnonKey) : null,
  )
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [token, setToken] = useState<string | null>(null)
  const [packs, setPacks] = useState<AdminPack[]>([])
  const [avatarParts, setAvatarParts] = useState<AvatarPart[]>([])
  const [json, setJson] = useState(samplePack)
  const [packPreview, setPackPreview] = useState<PackPreview | null>(null)
  const [message, setMessage] = useState('')
  const [avatarMessage, setAvatarMessage] = useState('')
  const [avatarFile, setAvatarFile] = useState<File | null>(null)
  const [avatarName, setAvatarName] = useState('New avatar part')
  const [avatarType, setAvatarType] = useState<AvatarPartType>('Clothes')
  const [supportsMale, setSupportsMale] = useState(true)
  const [supportsFemale, setSupportsFemale] = useState(true)
  const [activeAvatarPart, setActiveAvatarPart] = useState(true)
  const [maleTransform, setMaleTransform] = useState<AvatarTransform>({ x: 0, y: 0, scale: 1, rotation: 0 })
  const [femaleTransform, setFemaleTransform] = useState<AvatarTransform>({ x: 0, y: 0, scale: 1, rotation: 0 })

  async function signIn(event: FormEvent) {
    event.preventDefault()
    if (!client) {
      setMessage('Supabase env vars are not configured. Local dev admin endpoints are open.')
      return
    }

    const { data, error } = await client.auth.signInWithPassword({ email, password })
    if (error) {
      setMessage(error.message)
      return
    }

    setToken(data.session?.access_token ?? null)
    setMessage('Signed in.')
  }

  const adminFetch = useCallback(async (path: string, init: RequestInit = {}) => {
    const headers = new Headers(init.headers)
    if (!(init.body instanceof FormData)) {
      headers.set('Content-Type', 'application/json')
    }
    if (token) headers.set('Authorization', `Bearer ${token}`)
    return fetch(`${apiBase}${path}`, { ...init, headers })
  }, [token])

  const refreshPacks = useCallback(async () => {
    const response = await adminFetch('/api/admin/packs')
    if (response.ok) {
      setPacks(await response.json())
    }
  }, [adminFetch])

  const refreshAvatarParts = useCallback(async () => {
    const response = await adminFetch('/api/admin/avatar/parts')
    if (response.ok) {
      setAvatarParts(await response.json())
    }
  }, [adminFetch])

  useEffect(() => {
    void refreshPacks()
    void refreshAvatarParts()
  }, [refreshPacks, refreshAvatarParts])

  async function uploadPack(event: FormEvent) {
    event.preventDefault()
    try {
      const body = JSON.stringify(JSON.parse(json))
      const response = await adminFetch('/api/admin/packs', { method: 'POST', body })
      const payload = await response.json().catch(() => ({}))
      if (!response.ok) {
        setMessage(payload.errors?.join(' ') ?? 'Pack upload failed.')
        return
      }
      setMessage('Pack uploaded.')
      await refreshPacks()
    } catch {
      setMessage('The JSON is not valid.')
    }
  }

  async function validatePack() {
    try {
      const parsed = JSON.parse(json)
      const response = await adminFetch('/api/admin/packs/validate', {
        method: 'POST',
        body: JSON.stringify(parsed),
      })
      const validation = await response.json()
      const categories = Array.isArray(parsed.categories) ? parsed.categories : []
      setPackPreview({
        name: parsed.name || 'Untitled pack',
        language: parsed.language || 'unknown',
        categories: categories.length,
        rounds: categories.reduce((total: number, category: { rounds?: unknown[] }) => total + (Array.isArray(category.rounds) ? category.rounds.length : 0), 0),
        valid: Boolean(validation.valid),
        errors: validation.errors ?? [],
      })
      setMessage(validation.valid ? 'Pack looks ready.' : 'Pack needs fixes before upload.')
    } catch {
      setPackPreview({
        name: 'Invalid JSON',
        language: 'unknown',
        categories: 0,
        rounds: 0,
        valid: false,
        errors: ['The JSON is not valid.'],
      })
      setMessage('The JSON is not valid.')
    }
  }

  async function deletePack(id: string) {
    await adminFetch(`/api/admin/packs/${id}`, { method: 'DELETE' })
    await refreshPacks()
  }

  async function uploadAvatarPart(event: FormEvent) {
    event.preventDefault()
    if (!avatarFile) {
      setAvatarMessage('Choose a 1024x1024 transparent PNG.')
      return
    }

    const form = new FormData()
    form.set('file', avatarFile)
    form.set('name', avatarName)
    form.set('type', avatarType)
    form.set('supportsMale', String(supportsMale))
    form.set('supportsFemale', String(supportsFemale))
    form.set('active', String(activeAvatarPart))
    form.set('maleTransform', JSON.stringify(maleTransform))
    form.set('femaleTransform', JSON.stringify(femaleTransform))

    const response = await adminFetch('/api/admin/avatar/parts', { method: 'POST', body: form })
    const payload = await response.json().catch(() => ({}))
    if (!response.ok) {
      setAvatarMessage(payload.error ?? 'Avatar part upload failed.')
      return
    }

    setAvatarMessage('Avatar part uploaded.')
    setAvatarFile(null)
    await refreshAvatarParts()
  }

  async function saveAvatarPart(part: AvatarPart) {
    const response = await adminFetch(`/api/admin/avatar/parts/${part.id}`, {
      method: 'PUT',
      body: JSON.stringify({
        type: part.type,
        name: part.name,
        supportsMale: part.supportsMale,
        supportsFemale: part.supportsFemale,
        active: part.active,
        maleTransform: part.maleTransform,
        femaleTransform: part.femaleTransform,
      }),
    })
    setAvatarMessage(response.ok ? 'Avatar part saved.' : 'Could not save avatar part.')
    await refreshAvatarParts()
  }

  async function deleteAvatarPart(id: string) {
    await adminFetch(`/api/admin/avatar/parts/${id}`, { method: 'DELETE' })
    setAvatarMessage('Avatar part deleted.')
    await refreshAvatarParts()
  }

  return (
    <section className="admin-layout">
      <form className="panel" onSubmit={signIn}>
        <Shield className="panel-icon" />
        <h2>Admin sign-in</h2>
        <TextInput label="Email" value={email} onChange={setEmail} />
        <TextInput label="Password" value={password} onChange={setPassword} type="password" />
        <button className="primary" type="submit">
          Sign in
        </button>
        <p className="muted">Use Supabase Auth in production. Local dev works without credentials.</p>
      </form>

      <form className="panel wide-panel" onSubmit={uploadPack}>
        <Upload className="panel-icon" />
        <h2>Prompt pack JSON</h2>
        <textarea value={json} onChange={(event) => setJson(event.target.value)} spellCheck={false} />
        <div className="admin-row-actions">
          <button className="secondary" type="button" onClick={() => void validatePack()}>
            <Eye size={18} />
            Validate preview
          </button>
          <button className="primary" type="submit">
            <Upload size={18} />
            Upload pack
          </button>
        </div>
        {packPreview && (
          <div className={packPreview.valid ? 'pack-preview valid' : 'pack-preview invalid'}>
            <strong>{packPreview.name}</strong>
            <span>{packPreview.language} / {packPreview.categories} categories / {packPreview.rounds} rounds</span>
            {packPreview.errors.length > 0 && (
              <ul>
                {packPreview.errors.map((item) => <li key={item}>{item}</li>)}
              </ul>
            )}
          </div>
        )}
        {message && <p className="muted">{message}</p>}
      </form>

      <section className="panel wide-panel">
        <h2>Uploaded packs</h2>
        <div className="pack-list">
          {packs.map((pack) => (
            <div className="pack-row" key={pack.id}>
              <div>
                <strong>{pack.name}</strong>
                <small>
                  {pack.language} / {pack.categoryCount} categories / {pack.roundCount} rounds
                </small>
              </div>
              <button className="ghost" type="button" onClick={() => deletePack(pack.id)}>
                Delete
              </button>
            </div>
          ))}
        </div>
      </section>

      <form className="panel wide-panel avatar-admin-panel" onSubmit={uploadAvatarPart}>
        <ImagePlus className="panel-icon" />
        <h2>Avatar part upload</h2>
        <p className="muted">Upload a transparent 1024x1024 PNG, then position it for each supported gender.</p>
        <TextInput label="Part name" value={avatarName} onChange={setAvatarName} />
        <label className="field">
          Type
          <select value={avatarType} onChange={(event) => setAvatarType(event.target.value as AvatarPartType)}>
            <option value="Clothes">Clothes</option>
            <option value="Face">Face</option>
            <option value="Headwear">Hair / headwear</option>
          </select>
        </label>
        <label className="field">
          PNG file
          <input accept="image/png" type="file" onChange={(event) => setAvatarFile(event.target.files?.[0] ?? null)} />
        </label>
        <div className="admin-checks">
          <label><input checked={supportsMale} type="checkbox" onChange={(event) => setSupportsMale(event.target.checked)} /> Male</label>
          <label><input checked={supportsFemale} type="checkbox" onChange={(event) => setSupportsFemale(event.target.checked)} /> Female</label>
          <label><input checked={activeAvatarPart} type="checkbox" onChange={(event) => setActiveAvatarPart(event.target.checked)} /> Active</label>
        </div>
        <div className="avatar-editor-grid">
          {supportsMale && <TransformEditor label="Male transform" value={maleTransform} onChange={setMaleTransform} />}
          {supportsFemale && <TransformEditor label="Female transform" value={femaleTransform} onChange={setFemaleTransform} />}
        </div>
        <button className="primary" type="submit">
          <Upload size={18} />
          Upload avatar part
        </button>
        {avatarMessage && <p className="muted">{avatarMessage}</p>}
      </form>

      <section className="panel wide-panel">
        <h2>Avatar parts</h2>
        <div className="avatar-part-list">
          {avatarParts.map((part) => (
            <AdminAvatarPartRow
              key={part.id}
              part={part}
              onDelete={deleteAvatarPart}
              onSave={saveAvatarPart}
            />
          ))}
        </div>
      </section>
    </section>
  )
}

function TransformEditor({ label, value, onChange }: { label: string; value: AvatarTransform; onChange: (value: AvatarTransform) => void }) {
  return (
    <div className="transform-editor">
      <strong>{label}</strong>
      <NumberInput label="X" value={value.x} min={-320} max={320} onChange={(x) => onChange({ ...value, x })} />
      <NumberInput label="Y" value={value.y} min={-320} max={320} onChange={(y) => onChange({ ...value, y })} />
      <NumberInput label="Scale" value={value.scale} min={0.2} max={2.5} step={0.05} onChange={(scale) => onChange({ ...value, scale })} />
      <NumberInput label="Rotation" value={value.rotation} min={-180} max={180} onChange={(rotation) => onChange({ ...value, rotation })} />
    </div>
  )
}

function AdminAvatarPartRow({
  part,
  onSave,
  onDelete,
}: {
  part: AvatarPart
  onSave: (part: AvatarPart) => void
  onDelete: (id: string) => void
}) {
  const [draft, setDraft] = useState(part)

  useEffect(() => {
    setDraft(part)
  }, [part])

  return (
    <div className="avatar-part-row">
      <div className="avatar-part-preview">
        <AvatarView
          avatar={{
            gender: draft.supportsMale ? 'Male' : 'Female',
            skinColor: defaultAvatar.skinColor,
            clothesId: draft.type === 'Clothes' ? draft.id : null,
            faceId: draft.type === 'Face' ? draft.id : null,
            headwearId: draft.type === 'Headwear' ? draft.id : null,
          }}
          parts={[draft]}
          size="small"
        />
      </div>
      <div className="avatar-part-fields">
        <TextInput label="Name" value={draft.name} onChange={(name) => setDraft({ ...draft, name })} />
        <label className="field">
          Type
          <select value={draft.type} onChange={(event) => setDraft({ ...draft, type: event.target.value as AvatarPartType })}>
            <option value="Clothes">Clothes</option>
            <option value="Face">Face</option>
            <option value="Headwear">Hair / headwear</option>
          </select>
        </label>
        <div className="admin-checks">
          <label><input checked={draft.supportsMale} type="checkbox" onChange={(event) => setDraft({ ...draft, supportsMale: event.target.checked })} /> Male</label>
          <label><input checked={draft.supportsFemale} type="checkbox" onChange={(event) => setDraft({ ...draft, supportsFemale: event.target.checked })} /> Female</label>
          <label><input checked={draft.active} type="checkbox" onChange={(event) => setDraft({ ...draft, active: event.target.checked })} /> Active</label>
        </div>
        <div className="avatar-editor-grid">
          {draft.supportsMale && (
            <TransformEditor
              label="Male"
              value={draft.maleTransform ?? { x: 0, y: 0, scale: 1, rotation: 0 }}
              onChange={(maleTransform) => setDraft({ ...draft, maleTransform })}
            />
          )}
          {draft.supportsFemale && (
            <TransformEditor
              label="Female"
              value={draft.femaleTransform ?? { x: 0, y: 0, scale: 1, rotation: 0 }}
              onChange={(femaleTransform) => setDraft({ ...draft, femaleTransform })}
            />
          )}
        </div>
        <div className="admin-row-actions">
          <button className="secondary" type="button" onClick={() => onSave(draft)}>
            <Save size={18} />
            Save
          </button>
          <button className="ghost" type="button" onClick={() => onDelete(draft.id)}>
            <Trash2 size={18} />
            Delete
          </button>
        </div>
      </div>
    </div>
  )
}

function AnswerBoard({
  room,
  selected,
  onSelect,
}: {
  room: RoomSnapshot
  selected?: string
  onSelect?: (id: string) => void
}) {
  return (
    <div className={onSelect ? 'answer-grid' : 'answer-grid readonly'}>
      {room.answers.map((answer) => (
        <button
          className={[
            'answer-tile',
            selected === answer.id ? 'selected' : '',
            answer.isMine ? 'mine' : '',
            answer.isImposterAnswer ? 'imposter' : '',
          ].join(' ')}
          key={answer.id}
          type="button"
          disabled={Boolean(onSelect) && answer.isMine}
          onClick={() => onSelect?.(answer.id)}
        >
          <strong>{answer.text}</strong>
          {answer.authorName && <span>{answer.authorName}</span>}
        </button>
      ))}
    </div>
  )
}

type StandingBadge =
  | 'Winner'
  | 'Runner up'
  | 'Mirage'
  | 'Caught'
  | 'Correct vote'
  | 'Wrong vote'
  | 'Claimed'
  | 'Safe bet'
  | 'Penalty'
  | 'Safe'

type StandingRow = {
  player: Player
  rank: number
  delta: number
  badge: StandingBadge
  reasons: string[]
  isImposter: boolean
}

function RoundLeaderboard({ result, players, parts }: { result: RoundResult; players: Player[]; parts: AvatarPart[] }) {
  const standings = useMemo(() => buildStandings(players, result), [players, result])
  const miragePlayer = players.find((player) => player.id === result.imposterPlayerId)
  const mirageName = miragePlayer?.name ?? 'Someone'
  const jackpot = result.rollover > 0
  const tableCaught = result.events.some((event) => event.reason === 'Caught by confidence votes')
  const biggestGain = standings.filter((row) => row.delta > 0).sort((a, b) => b.delta - a.delta)[0]
  const biggestLoss = standings.filter((row) => row.delta < 0).sort((a, b) => a.delta - b.delta)[0]

  return (
    <div className={jackpot ? 'leaderboard-results jackpot-results' : 'leaderboard-results payout-results'}>
      <section className={jackpot ? 'reveal-card jackpot' : 'reveal-card caught'}>
        <span className={jackpot ? 'screen-tag dark-tag' : 'screen-tag'}>
          {jackpot ? `Round ${result.roundNumber} - Mirage held` : `Round ${result.roundNumber} - Payout`}
        </span>
        <div className="reveal-body">
          <AvatarView avatar={miragePlayer?.avatar ?? defaultAvatar} parts={parts} size="small" />
          <div>
            <h2>{jackpot ? 'No one saw through it' : 'The mirage was found'}</h2>
            <p>
              <strong>{mirageName}</strong> had <strong>{result.alternatePrompt}</strong>
              {' '}while the table saw <strong>{result.majorityPrompt}</strong>.
            </p>
          </div>
        </div>
        <div className="result-pills" aria-label="Round payout details">
          <span>Pool {result.pool}</span>
          <span>Rollover {result.rollover}</span>
          <span>{tableCaught ? 'Caught' : 'Not caught'}</span>
        </div>
        {(biggestGain || biggestLoss) && (
          <div className="mover-strip" aria-label="Round movers">
            {biggestGain && (
              <div>
                <span>Biggest rise</span>
                <strong>{biggestGain.player.name}</strong>
                <b>+{biggestGain.delta}</b>
              </div>
            )}
            {biggestLoss && (
              <div>
                <span>Hardest hit</span>
                <strong>{biggestLoss.player.name}</strong>
                <b>{biggestLoss.delta}</b>
              </div>
            )}
          </div>
        )}
      </section>

      <section className="standings-card" aria-label="Round standings">
        <div className="standings-heading">
          <div>
            <span>Standings</span>
            <h3>After round {result.roundNumber}</h3>
          </div>
          <b>{jackpot ? 'Jackpot round' : 'Score movement'}</b>
        </div>
        <LeaderboardRows rows={standings} parts={parts} showDelta />
      </section>

      {result.highlights.length > 0 && (
        <section className="round-notes" aria-label="Round notes">
          {result.highlights.map((highlight) => (
            <p className="highlight" key={highlight}>{friendlyHighlight(highlight)}</p>
          ))}
        </section>
      )}
    </div>
  )
}

function LeaderboardRows({ rows, parts, showDelta }: { rows: StandingRow[]; parts: AvatarPart[]; showDelta?: boolean }) {
  return (
    <div className="leaderboard-list">
      {rows.map((row) => (
        <div className={row.isImposter ? 'leaderboard-row imposter-row' : 'leaderboard-row'} key={row.player.id}>
          <div className="rank-medal">{row.rank}</div>
          <AvatarView avatar={row.player.avatar} parts={parts} size="mini" />
          <div className="leader-meta">
            <strong>{row.player.name}</strong>
            <span className={`leader-badge ${badgeClass(row.badge)}`}>{row.badge}</span>
            {showDelta && row.reasons.length > 0 && <small className="leader-reasons">{row.reasons.join(' + ')}</small>}
          </div>
          <div className="score-stack">
            {showDelta && <span className={row.delta >= 0 ? 'round-delta gain' : 'round-delta loss'}>{formatDelta(row.delta)}</span>}
            <b>{row.player.score}</b>
          </div>
        </div>
      ))}
    </div>
  )
}

function FinalLeaderboard({ players, parts }: { players: Player[]; parts: AvatarPart[] }) {
  const ranked = useMemo(() => buildFinalStandings(players), [players])
  const winner = ranked[0]
  if (!winner) {
    return null
  }

  return (
    <div className="final-leaderboard">
      <section className="winner-hero">
        <AvatarView avatar={winner.player.avatar} parts={parts} size="small" />
        <div>
          <span>Winner</span>
          <h2>{winner.player.name}</h2>
          <p>saw clearest through the mirage</p>
        </div>
        <b>{winner.player.score}<small> pts</small></b>
      </section>

      <section className="standings-card final-standings" aria-label="Final standings">
        <div className="standings-heading">
          <div>
            <span>Final board</span>
            <h3>Full standings</h3>
          </div>
          <b>{ranked.length} players</b>
        </div>
        <LeaderboardRows rows={ranked} parts={parts} />
      </section>
    </div>
  )
}


function ClaimList({ players }: { players: Player[] }) {
  const claimers = players.filter((player) => player.selfReported)
  return (
    <div className="claim-list">
      <span>Mirage claims</span>
      {claimers.length === 0 ? (
        <small>No one has stepped into the heat yet.</small>
      ) : (
        <div>
          {claimers.map((player) => (
            <b key={player.id}>{player.name}</b>
          ))}
        </div>
      )}
    </div>
  )
}


function claimText(players: Player[]) {
  const claimers = players.filter((player) => player.selfReported).map((player) => player.name)
  if (claimers.length === 0) {
    return 'No one claims they saw it. Trust the words.'
  }

  if (claimers.length === 1) {
    return `${claimers[0]} claims they saw it. Use that, or don't.`
  }

  return `${claimers.slice(0, 2).join(' & ')} claim they saw it. Use that, or don't.`
}

function friendlyHighlight(highlight: string) {
  return highlight
    .replace('1 player(s)', '1 player')
    .replace(/(\d+) player\(s\)/g, '$1 players')
}

function buildStandings(players: Player[], result: RoundResult): StandingRow[] {
  const deltas = new Map<string, number>()
  for (const event of result.events) {
    deltas.set(event.playerId, (deltas.get(event.playerId) ?? 0) + event.delta)
  }

  return [...players]
    .sort((a, b) => b.score - a.score || a.name.localeCompare(b.name))
    .map((player, index) => {
      const events = result.events.filter((event) => event.playerId === player.id)
      return {
        player,
        rank: index + 1,
        delta: deltas.get(player.id) ?? 0,
        badge: roundBadgeFor(player, result, events),
        reasons: eventReasons(events),
        isImposter: player.id === result.imposterPlayerId,
      }
    })
}

function buildFinalStandings(players: Player[]): StandingRow[] {
  return [...players]
    .sort((a, b) => b.score - a.score || a.name.localeCompare(b.name))
    .map((player, index) => ({
      player,
      rank: index + 1,
      delta: 0,
      badge: index === 0 ? 'Winner' : index === 1 ? 'Runner up' : 'Safe',
      reasons: [],
      isImposter: false,
    }))
}

function roundBadgeFor(player: Player, result: RoundResult, events: ScoreEvent[]): StandingBadge {
  if (player.id === result.imposterPlayerId) {
    return events.some((event) => event.reason === 'Caught by confidence votes') ? 'Caught' : 'Mirage'
  }

  if (events.some((event) => event.reason === 'Correct vote payout' || event.reason === 'Pool rounding')) {
    return 'Correct vote'
  }

  if (events.some((event) => event.reason === 'Wrong confidence vote')) {
    return 'Wrong vote'
  }

  if (events.some((event) => event.reason === 'False self-report')) {
    return 'Claimed'
  }

  if (events.some((event) => event.reason === 'Correct safe bet')) {
    return 'Safe bet'
  }

  if (events.some((event) => event.reason === 'Wrong safe bet')) {
    return 'Penalty'
  }

  if (events.some((event) => event.reason === 'Obvious answer')) {
    return 'Penalty'
  }

  if (events.some((event) => event.reason === 'Copycat/converged answer')) {
    return 'Penalty'
  }

  if (events.some((event) => event.reason.includes('self-report'))) {
    return 'Claimed'
  }

  if (events.some((event) => event.delta < 0)) {
    return 'Penalty'
  }

  return 'Safe'
}

function eventReasons(events: ScoreEvent[]) {
  return events.map((event) => `${event.detail || friendlyReason(event.reason)} (${formatDelta(event.delta)})`)
}

function friendlyReason(reason: string) {
  switch (reason) {
    case 'Wrong confidence vote':
      return 'Wrong vote penalty'
    case 'Caught by confidence votes':
      return 'Caught penalty'
    case 'False self-report':
      return 'False claim penalty'
    case 'Correct self-report':
      return 'Correct self-report'
    case 'Correct safe bet':
      return 'Correct safe bet'
    case 'Wrong safe bet':
      return 'Wrong safe bet penalty'
    case 'Obvious answer':
      return 'Obvious answer penalty'
    case 'Copycat/converged answer':
      return 'Copycat penalty'
    case 'Undetected imposter payout':
      return 'Mirage payout'
    case 'Correct vote payout':
      return 'Correct vote payout'
    case 'Pool rounding':
      return 'Pool rounding'
    default:
      return reason
  }
}

function formatDelta(delta: number) {
  if (delta > 0) return `+${delta}`
  return String(delta)
}

function badgeClass(badge: StandingBadge) {
  return badge.toLowerCase().replace(/\s+/g, '-')
}

function TextInput({
  label,
  value,
  onChange,
  type = 'text',
  autoComplete,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  autoComplete?: string
}) {
  return (
    <label className="field">
      {label}
      <input value={value} onChange={(event) => onChange(event.target.value)} type={type} autoComplete={autoComplete} />
    </label>
  )
}

function NumberInput({
  label,
  value,
  min,
  max,
  step = 1,
  onChange,
}: {
  label: string
  value: number
  min: number
  max: number
  step?: number
  onChange: (value: number) => void
}) {
  return (
    <label className="field">
      {label}
      <input
        value={value}
        min={min}
        max={max}
        step={step}
        onChange={(event) => onChange(Number(event.target.value))}
        type="number"
      />
    </label>
  )
}

function useCountdown(endsAt?: string) {
  const [now, setNow] = useState(Date.now())
  const timerRef = useRef<number | null>(null)

  useEffect(() => {
    timerRef.current = window.setInterval(() => setNow(Date.now()), 500)
    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current)
    }
  }, [])

  if (!endsAt) return '--'
  const remaining = Math.max(0, Math.ceil((new Date(endsAt).getTime() - now) / 1000))
  return `${remaining}s`
}

function statusFor(player: Player, phase: RoomPhase) {
  if (phase === 'Lobby') return player.ready ? 'ready' : 'not ready'
  if (phase === 'Answer') return player.submittedAnswer ? 'answered' : 'thinking'
  if (phase === 'SelfReport') return player.selfReported ? 'claimed' : player.safeBet ? 'bet safe' : player.selfReportDone ? 'neutral' : 'watching'
  if (phase === 'Vote') return player.voted ? 'voted' : 'voting'
  return player.connected ? 'ready' : 'away'
}

function playerStatusText(player: Player, phase: RoomPhase) {
  const status = player.waitingForNextRound ? 'next round' : statusFor(player, phase)
  return player.isHost ? `Host - ${status}` : status
}

function phaseEyebrow(phase: RoomPhase) {
  if (phase === 'SelfReport') return 'Self report'
  if (phase === 'GameOver') return 'Game over'
  return phase
}

function hostAdvanceLabel(phase: RoomPhase) {
  if (phase === 'Answer') return 'Reveal answers'
  if (phase === 'SelfReport') return 'Open voting'
  if (phase === 'Vote') return 'Reveal scores'
  if (phase === 'Results') return 'Next round'
  return 'Continue'
}

function loadSavedAvatar(): PlayerAvatar {
  try {
    const saved = JSON.parse(localStorage.getItem('sarab:avatar') ?? '')
    return normalizeAvatarSelection(saved, [])
  } catch {
    return defaultAvatar
  }
}

function normalizeAvatarSelection(avatar: PlayerAvatar, parts: AvatarPart[]): PlayerAvatar {
  const normalized: PlayerAvatar = {
    gender: avatar.gender === 'Female' ? 'Female' : 'Male',
    skinColor: isValidHexColor(avatar.skinColor) ? avatar.skinColor.toUpperCase() : defaultAvatar.skinColor,
    clothesId: avatar.clothesId ?? null,
    faceId: avatar.faceId ?? null,
    headwearId: avatar.headwearId ?? null,
  }

  for (const type of ['Clothes', 'Face', 'Headwear'] as AvatarPartType[]) {
    const field = avatarField(type)
    const current = parts.find((part) => part.id === normalized[field])
    if (!current || !partSupportsGender(current, normalized.gender) || !current.active) {
      normalized[field] = compatibleParts(parts, type, normalized.gender)[0]?.id ?? null
    }
  }

  return normalized
}

function cycleAvatarPart(avatar: PlayerAvatar, parts: AvatarPart[], type: AvatarPartType, direction: -1 | 1) {
  const options = compatibleParts(parts, type, avatar.gender)
  if (options.length === 0) {
    return { ...avatar, [avatarField(type)]: null }
  }

  const field = avatarField(type)
  const currentIndex = options.findIndex((part) => part.id === avatar[field])
  const nextIndex = currentIndex < 0
    ? 0
    : (currentIndex + direction + options.length) % options.length

  return { ...avatar, [field]: options[nextIndex].id }
}

function compatibleParts(parts: AvatarPart[], type: AvatarPartType, gender: AvatarGender) {
  return parts
    .filter((part) => part.active && part.type === type && partSupportsGender(part, gender))
    .sort((a, b) => a.name.localeCompare(b.name))
}

function partSupportsGender(part: AvatarPart, gender: AvatarGender) {
  return gender === 'Male' ? part.supportsMale : part.supportsFemale
}

function selectedAvatarParts(avatar: PlayerAvatar, parts: AvatarPart[]) {
  const selected: Array<{ part: AvatarPart; transform?: AvatarTransform | null }> = []
  for (const [type, id] of [
    ['Clothes', avatar.clothesId],
    ['Face', avatar.faceId],
    ['Headwear', avatar.headwearId],
  ] as Array<[AvatarPartType, string | null | undefined]>) {
    const part = parts.find((item) => item.id === id && item.type === type && partSupportsGender(item, avatar.gender))
    if (part) {
      selected.push({
        part,
        transform: avatar.gender === 'Male' ? part.maleTransform : part.femaleTransform,
      })
    }
  }

  return selected
}

function avatarTransform(transform?: AvatarTransform | null) {
  const value = transform ?? { x: 0, y: 0, scale: 1, rotation: 0 }
  return `translate(${value.x} ${value.y}) translate(512 512) rotate(${value.rotation}) scale(${value.scale}) translate(-512 -512)`
}

function avatarField(type: AvatarPartType): 'clothesId' | 'faceId' | 'headwearId' {
  if (type === 'Face') return 'faceId'
  if (type === 'Headwear') return 'headwearId'
  return 'clothesId'
}

function isValidHexColor(value: string) {
  return /^#[0-9A-F]{6}$/i.test(value)
}

type SoundCue = 'click' | 'ready' | 'success' | 'start' | 'submit' | 'claim' | 'vote' | 'reveal' | 'timer'

function playSound(cue: SoundCue, muted: boolean) {
  if (muted || typeof window === 'undefined') {
    return
  }

  const audioWindow = window as Window & { webkitAudioContext?: typeof AudioContext }
  const AudioContextClass = window.AudioContext || audioWindow.webkitAudioContext
  if (!AudioContextClass) {
    return
  }

  const context = getAudioContext(AudioContextClass)
  const now = context.currentTime
  const pattern: Record<SoundCue, Array<[number, number, number]>> = {
    click: [[420, 0, 0.045]],
    ready: [[520, 0, 0.055], [760, 0.055, 0.08]],
    success: [[480, 0, 0.07], [720, 0.08, 0.1]],
    start: [[360, 0, 0.06], [560, 0.065, 0.08], [820, 0.14, 0.11]],
    submit: [[620, 0, 0.08]],
    claim: [[240, 0, 0.12], [430, 0.1, 0.09]],
    vote: [[540, 0, 0.055], [460, 0.055, 0.055]],
    reveal: [[300, 0, 0.08], [680, 0.09, 0.15]],
    timer: [[880, 0, 0.06], [880, 0.12, 0.06]],
  }

  for (const [frequency, delay, duration] of pattern[cue]) {
    const oscillator = context.createOscillator()
    const gain = context.createGain()
    oscillator.type = cue === 'timer' ? 'square' : 'sine'
    oscillator.frequency.setValueAtTime(frequency, now + delay)
    gain.gain.setValueAtTime(0.0001, now + delay)
    gain.gain.exponentialRampToValueAtTime(0.08, now + delay + 0.01)
    gain.gain.exponentialRampToValueAtTime(0.0001, now + delay + duration)
    oscillator.connect(gain).connect(context.destination)
    oscillator.start(now + delay)
    oscillator.stop(now + delay + duration + 0.02)
  }
}

let audioContext: AudioContext | null = null

function getAudioContext(AudioContextClass: typeof AudioContext) {
  audioContext ??= new AudioContextClass()
  if (audioContext.state === 'suspended') {
    void audioContext.resume()
  }
  return audioContext
}

const samplePack = `{
  "schemaVersion": 1,
  "language": "en",
  "name": "Family Pack",
  "categories": [
    {
      "id": "nature",
      "name": "Nature",
      "rounds": [
        {
          "id": "ocean-lake",
          "prompts": ["Ocean", "Lake"],
          "closeness": 82,
          "obviousAnswers": {
            "0": ["water", "waves", "blue"],
            "1": ["water", "fish", "shore"]
          }
        }
      ]
    }
  ]
}`

export default App
