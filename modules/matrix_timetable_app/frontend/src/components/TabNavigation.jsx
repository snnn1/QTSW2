import { STREAMS } from '../utils/constants'

export default function TabNavigation({ activeTab, setActiveTab }) {
  return (
    <div className="flex gap-2 mb-6 border-b border-gray-700 overflow-x-auto">
      <button
        onClick={() => setActiveTab('timetable')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'timetable'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        Timetable
      </button>
      <button
        onClick={() => setActiveTab('master')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'master'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        Masterstream
      </button>
      {STREAMS.map(stream => (
        <button
          key={stream}
          onClick={() => setActiveTab(stream)}
          className={`px-4 py-2 font-medium whitespace-nowrap ${
            activeTab === stream
              ? 'border-b-2 border-blue-500 text-blue-400'
              : 'text-gray-400 hover:text-gray-300'
          }`}
        >
          {stream}
        </button>
      ))}
      <button
        onClick={() => setActiveTab('time')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'time'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        Time
      </button>
      <button
        onClick={() => setActiveTab('day')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'day'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        DOW
      </button>
      <button
        onClick={() => setActiveTab('dom')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'dom'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        DOM
      </button>
      <button
        onClick={() => setActiveTab('month')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'month'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        Month
      </button>
      <button
        onClick={() => setActiveTab('year')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'year'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        Year
      </button>
      <button
        onClick={() => setActiveTab('stats')}
        className={`px-4 py-2 font-medium whitespace-nowrap ${
          activeTab === 'stats'
            ? 'border-b-2 border-blue-500 text-blue-400'
            : 'text-gray-400 hover:text-gray-300'
        }`}
      >
        Stats
      </button>
    </div>
  )
}

























