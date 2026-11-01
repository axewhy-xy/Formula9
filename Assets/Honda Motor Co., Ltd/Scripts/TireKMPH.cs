using UnityEngine;

namespace Honda
{
    public class TireKMPH : MonoBehaviour
    {
        [SerializeField]
        private Component _textValue;

        private float _speed = 0f;
        public float KmPerHour => _speed;

        // Start is called before the first frame update
        void Start()
        {
        }

        // Update is called once per frame
        void Update()
        {
            UpdateText();
        }

        public void AddSpeed( float value )
        {
            _speed += value;
        }

        private void UpdateText()
        {
            if ( _textValue == null ) return;
            var property = _textValue.GetType().GetProperty( "text" );
            if ( property != null && property.CanWrite )
                property.SetValue( _textValue, _speed.ToString( "F3" ) );
        }
    }
}
