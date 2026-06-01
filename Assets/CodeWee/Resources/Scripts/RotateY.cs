using UnityEngine;

namespace CodeWee
{
	public class RotateY : MonoBehaviour
	{
		public float speed = 90;
		// Start is called before the first frame update
		void Start()
		{

		}

		// Update is called once per frame
		void Update()
		{
			transform.Rotate(Vector3.up, speed * Time.deltaTime);
		}
	}
}
